using System.Diagnostics;
using System.Text.Json;
using SemanticAssistant.Adapters.Mcp;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class IsolatedSchemaValidatorTests
{
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static JsonElement Schema => Json("""{"type":"object","properties":{"message":{"type":"string","minLength":1}},"required":["message"],"additionalProperties":false}""");
    private static string Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticAssistant.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new IOException(), "tests", "SemanticAssistant.FixtureHost", "bin", "Release", "net10.0", "SemanticAssistant.FixtureHost.dll");
    }
    private static void Exited(int? pid)
    {
        Assert.NotNull(pid);
        try { using var process = Process.GetProcessById(pid.Value); Assert.True(process.HasExited, "Our schema helper is still alive."); }
        catch (ArgumentException) { /* This known helper PID no longer exists. */ }
    }
    [Fact]
    public async Task ActualCliWorkerAcceptsValidArgumentsAndTerminates()
    {
        var validator = new IsolatedSchemaValidator();
        await validator.ValidateAsync(Schema, ContractJson.Element(new { message = "合成訊息" }), default);
        Exited(validator.LastProcessId);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":4}")]
    [InlineData("{\"message\":\"\"}")]
    [InlineData("{\"message\":\"x\",\"extra\":true}")]
    public async Task ActualWorkerDeniesInvalidArgumentsWithoutReturningInput(string json)
    {
        var validator = new IsolatedSchemaValidator();
        var error = await Assert.ThrowsAsync<SchemaValidationException>(() => validator.ValidateAsync(Schema, Json(json), default));
        Assert.Equal("invalid_arguments", error.Code); Assert.DoesNotContain(json, error.Message); Exited(validator.LastProcessId);
    }
    [Fact]
    public async Task LocalReferencesAndExplicitDraft202012WorkWithoutRemoteFetch()
    {
        var schema = Json("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","$defs":{"message":{"type":"string"}},"properties":{"message":{"$ref":"#/$defs/message"}},"required":["message"]}""");
        var validator = new IsolatedSchemaValidator();
        await validator.ValidateAsync(schema, Json("{\"message\":\"合成\"}"), default); Exited(validator.LastProcessId);
    }
    [Theory]
    [InlineData("http://json-schema.org/draft-07/schema#", true)]
    [InlineData("https://json-schema.org/draft/2020-12/schema", false)]
    public async Task DeclaredDialectControlsReferenceSiblingSemantics(string dialect, bool accepts)
    {
        var schema = Json("{\"$schema\":\"" + dialect + "\",\"$ref\":\"#/definitions/record\",\"type\":\"integer\",\"definitions\":{\"record\":{\"type\":\"object\"}}}");
        var validator = new IsolatedSchemaValidator();
        if (accepts) await validator.ValidateAsync(schema, Json("{}"), default);
        else Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<SchemaValidationException>(() => validator.ValidateAsync(schema, Json("{}"), default))).Code);
        Exited(validator.LastProcessId);
    }
    [Theory]
    [InlineData("{\"$ref\":\"https://example.invalid/private\"}")]
    [InlineData("{\"$dynamicRef\":\"https://example.invalid/private\"}")]
    [InlineData("{\"type\":\"object\",\"type\":\"array\"}")]
    public async Task UnsafeSchemaIsDeniedBeforeLaunchingHelper(string json)
    {
        var validator = new IsolatedSchemaValidator();
        await Assert.ThrowsAnyAsync<Exception>(() => validator.ValidateAsync(Json(json), Json("{}"), default));
        Assert.Null(validator.LastProcessId);
    }
    [Fact]
    public async Task DuplicateArgumentPropertiesAreDeniedBeforeLaunching()
    {
        var validator = new IsolatedSchemaValidator();
        await Assert.ThrowsAsync<McpException>(() => validator.ValidateAsync(Schema, Json("{\"message\":\"x\",\"message\":\"y\"}"), default));
        Assert.Null(validator.LastProcessId);
    }
    [Fact]
    public async Task MissingHelperFailsClosed()
    {
        var validator = new IsolatedSchemaValidator(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll"));
        var error = await Assert.ThrowsAsync<SchemaValidationException>(() => validator.ValidateAsync(Schema, Json("{}"), default));
        Assert.Equal("worker_unavailable", error.Code); Assert.Null(validator.LastProcessId);
    }
    [Fact]
    public async Task HungOwnHelperIsKilledAtWallDeadline()
    {
        var validator = new IsolatedSchemaValidator(Fixture(), TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        // Includes process startup, shared-runner scheduling and two bounded cleanup waits.
        // The worker deadline remains 500ms; total completion is not a real-time guarantee.
        var error = await Assert.ThrowsAsync<SchemaValidationException>(() => validator.ValidateAsync(Json("{\"type\":\"object\",\"x-fixture-hang\":true}"), Json("{}"), default)
            .WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal("validation_timeout", error.Code); Assert.InRange(watch.Elapsed.TotalSeconds, 0, 8); Exited(validator.LastProcessId);
    }
    [Fact]
    public async Task CancellationWaitsForOwnHelperCleanupAndDoesNotPublishResult()
    {
        var validator = new IsolatedSchemaValidator(Fixture()); using var cancellation = new CancellationTokenSource();
        var work = validator.ValidateAsync(Json("{\"type\":\"object\",\"x-fixture-hang\":true}"), Json("{}"), cancellation.Token);
        while (validator.LastProcessId is null && !work.IsCompleted) await Task.Delay(10);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Exited(validator.LastProcessId);
    }
    [Theory]
    [InlineData("x-fixture-bad-response")]
    [InlineData("x-fixture-oversize-response")]
    [InlineData("wrong-nonce")]
    public async Task UntrustedHelperReplyCannotAuthorizeArguments(string mode)
    {
        var validator = new IsolatedSchemaValidator(Fixture());
        var error = await Assert.ThrowsAsync<SchemaValidationException>(() => validator.ValidateAsync(ContractJson.Element(new Dictionary<string, object>
        { ["type"] = "object", [mode] = true }), Json("{}"), default));
        Assert.DoesNotContain("synthetic-sensitive", error.Message); Exited(validator.LastProcessId);
    }
    [Fact]
    public async Task WindowsWorkerCpuBudgetStopsOwnHelperWhileParentRemainsResponsive()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows-only Job guarantee, not a claimed portable sandbox.
        var validator = new IsolatedSchemaValidator(Fixture(), TimeSpan.FromSeconds(8)); var watch = Stopwatch.StartNew();
        var work = validator.ValidateAsync(Json("{\"type\":\"object\",\"x-fixture-spin\":true}"), Json("{}"), default);
        var ticks = 0;
        while (!work.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(9)) { await Task.Delay(50); ticks++; }
        var error = await Assert.ThrowsAsync<SchemaValidationException>(() => work);
        Assert.Equal("validation_cpu_limit", error.Code); Assert.True(ticks >= 5);
        Assert.InRange(validator.LastCpuTime?.TotalSeconds ?? 0, 1.5, 3.5);
        // CPU budget is not wall time. Allow the configured 8s wall deadline plus bounded pipe cleanup.
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.2, 11);
        Exited(validator.LastProcessId);
    }
    [Fact]
    public async Task ActualRegexSchemaRunsInDisposableWorkerAndCannotHangParent()
    {
        var validator = new IsolatedSchemaValidator(); var watch = Stopwatch.StartNew();
        var schema = Json("""{"type":"object","properties":{"message":{"type":"string","pattern":"^(a+)+$"}},"required":["message"]}""");
        await Assert.ThrowsAsync<SchemaValidationException>(() => validator.ValidateAsync(schema, ContractJson.Element(new { message = new string('a', 8000) + "!" }), default));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 8); Exited(validator.LastProcessId);
    }
}
