using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.AiAcceptance;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class AiAcceptanceTests
{
    private static AiAcceptanceProfile Profile => new(1,
        new("openai-compatible", "https://example.invalid/v1/chat/completions", "synthetic-ai"), 10);

    [Fact]
    public async Task DefaultPreflightDoesNotReadCredentialsOrSendAnyHttp()
    {
        var credentials = new Credentials();
        var handler = new FixtureHandler();
        var report = await new AiAcceptanceVerifier(Profile, credentials, handler).RunAsync();
        Assert.True(report.LocalPreflightPassed);
        Assert.False(report.LiveRequested);
        Assert.False(report.ConfiguredEndpointVerified);
        Assert.Equal(0, credentials.Reads);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(4, report.Gates.Count(g => g.Status == "passed"));
        Assert.Equal(4, report.Gates.Count(g => g.Status == "not-run"));
        Assert.All(report.Gates, g => Assert.Equal(0, g.ModelReads + g.Queries + g.HttpRequests + g.CredentialReads));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task MissingEgressCategoryStopsBeforeCredentialsOrTransport(bool message, bool metadata, bool query)
    {
        var credentials = new Credentials(); var handler = new FixtureHandler();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AiAcceptanceVerifier(Profile, credentials, handler)
            .RunAsync(true, message, metadata, query));
        Assert.Equal(0, credentials.Reads); Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task InjectedProtocolExercisesMultiToolsFragmentsRoundtripCancelAndRevocationWithoutClaimingLiveProvider()
    {
        var handler = new FixtureHandler();
        var report = await new AiAcceptanceVerifier(Profile, new Credentials(), handler).RunAsync(true, true, true, true);
        Assert.All(report.Gates, g => Assert.Equal("passed", g.Status));
        Assert.False(report.ConfiguredEndpointVerified);
        Assert.False(report.GeneralModelQualityVerified);
        Assert.Equal("injected-test-transport", report.Transport);
        Assert.Equal(6, handler.Requests); // ping, three tool rounds, cancel, revoke.
        Assert.True(handler.SawMultiToolResults);
        Assert.True(handler.SawQueryRows);
        Assert.Equal(0, report.Gates.Single(g => g.Id == "message-only").ModelReads);
        Assert.Equal("cancelled", report.Gates.Single(g => g.Id == "cancel-on-text").Code);
        Assert.Equal("stale_permission", report.Gates.Single(g => g.Id == "revoke-on-text").Code);
    }

    [Fact]
    public async Task ProviderSkippingToolsDoesNotPassRoundtripGate()
    {
        var report = await new AiAcceptanceVerifier(Profile, new Credentials(), new FixtureHandler { SkipTools = true })
            .RunAsync(true, true, true, true);
        Assert.Equal("failed", report.Gates.Single(g => g.Id == "tool-roundtrip").Status);
        Assert.Equal(0, report.Gates.Single(g => g.Id == "tool-roundtrip").Queries);
    }

    [Fact]
    public async Task QueryBeforeMetadataCannotPassEvenWhenFinalResponseAndBothToolsSucceed()
    {
        var report = await new AiAcceptanceVerifier(Profile, new Credentials(), new FixtureHandler { QueryBeforeMetadata = true })
            .RunAsync(true, true, true, true);
        Assert.Equal("failed", report.Gates.Single(g => g.Id == "tool-roundtrip").Status);
        Assert.Equal(1, report.Gates.Single(g => g.Id == "tool-roundtrip").Queries);
        Assert.All(report.Gates.Where(g => g.Id is "cancel-on-text" or "revoke-on-text"), g => Assert.Equal("not-run", g.Status));
    }

    [Fact]
    public async Task RejectedToolCannotBeHiddenByLaterSuccessfulQueryAndResponse()
    {
        var report = await new AiAcceptanceVerifier(Profile, new Credentials(), new FixtureHandler { IncludeUnknownTool = true })
            .RunAsync(true, true, true, true);
        Assert.Equal("failed", report.Gates.Single(g => g.Id == "tool-roundtrip").Status);
        Assert.Equal(1, report.Gates.Single(g => g.Id == "tool-roundtrip").Queries);
    }

    [Fact]
    public async Task RemoteErrorBodiesAndCredentialValuesNeverEnterVerificationReport()
    {
        var report = await new AiAcceptanceVerifier(Profile, new Credentials(), new FixtureHandler { Fail = true })
            .RunAsync(true, true, true, true);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("synthetic-sensitive-key", json);
        Assert.DoesNotContain("remote-sensitive-body", json);
        Assert.DoesNotContain(Profile.Provider.Endpoint, json);
        Assert.DoesNotContain(DemoModel.SupportedQuery, json);
        Assert.False(report.ConfiguredEndpointVerified);
        Assert.Equal("provider_error", report.Gates.Single(g => g.Id == "message-only").Code);
    }

    [Fact]
    public async Task PreCancelledRunNeverReadsCredentialOrLaunchesTransport()
    {
        var credentials = new Credentials(); var handler = new FixtureHandler();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiAcceptanceVerifier(Profile, credentials, handler)
            .RunAsync(token: cancel.Token));
        Assert.Equal(0, credentials.Reads); Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("{\"FormatVersion\":1,\"Provider\":null,\"TimeoutSeconds\":60}")]
    [InlineData("{\"FormatVersion\":1,\"TimeoutSeconds\":60}")]
    [InlineData("{\"FormatVersion\":1,\"FormatVersion\":1,\"Provider\":{},\"TimeoutSeconds\":60}")]
    [InlineData("{\"FormatVersion\":1,\"Provider\":{},\"TimeoutSeconds\":60,\"ApiKey\":\"synthetic-sensitive-key\"}")]
    public void ProfileRejectsMissingDuplicateAndSecretFields(string json) => Assert.ThrowsAny<Exception>(() => AiAcceptanceProfile.Parse(json));

    [Theory]
    [InlineData("--live", "--profile", "fixture.json", "--output-dir", "out")]
    [InlineData("--preflight", "--allow-message-egress", "--profile", "fixture.json", "--output-dir", "out")]
    [InlineData("--preflight", "--live", "--profile", "fixture.json", "--output-dir", "out")]
    [InlineData("--preflight", "--profile", "fixture.json", "--profile", "other.json", "--output-dir", "out")]
    [InlineData("--preflight", "--api-key", "synthetic-sensitive-key")]
    public void ArgumentsRejectAmbiguousModesDuplicateOptionsAndCredentials(params string[] args) =>
        Assert.Throws<ArgumentException>(() => AiAcceptanceArguments.Parse(args));

    [Fact]
    public void ExistingRunDirectoryCannotBeOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SemanticAssistant-ai-acceptance", Guid.NewGuid().ToString("N"));
        using (var output = AcceptanceReportFile.OpenNew(directory)) output.Write(Encoding.UTF8.GetBytes("synthetic-original"));
        Assert.Throws<IOException>(() => AcceptanceReportFile.OpenNew(directory));
        Assert.Equal("synthetic-original", File.ReadAllText(Path.Combine(directory, "verification.json")));
    }

    private sealed class Credentials : ICredentialSource
    {
        public int Reads { get; private set; }
        public string? ReadSecret(ProviderIdentity identity) { Reads++; return "synthetic-sensitive-key"; }
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public bool SkipTools { get; init; }
        public bool Fail { get; init; }
        public bool QueryBeforeMetadata { get; init; }
        public bool IncludeUnknownTool { get; init; }
        public int Requests { get; private set; }
        public bool SawMultiToolResults { get; private set; }
        public bool SawQueryRows { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("remote-sensitive-body") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var user = messages.Single(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            var marker = Regex.Match(user, "AI_SYNTHETIC_VERIFIED_[a-f0-9]{32}").Value;
            var results = messages.Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
            SawMultiToolResults |= results.Length >= 2;
            var queryReturned = results.Any(r => r.GetProperty("content").GetString()!.Contains("\"Rows\":", StringComparison.Ordinal));
            SawQueryRows |= queryReturned;
            string sse;
            if (!SkipTools && user.Contains(DemoModel.SupportedQuery, StringComparison.Ordinal) &&
                (!queryReturned || QueryBeforeMetadata && results.Length == 1))
            {
                var calls = results.Length == 0
                    ? new[] { ("table-sales", "get_table_details", "{\"table\":\"銷售\"}"), ("table-dates", "get_table_details", "{\"table\":\"日期\"}") }
                    : new[] { ("query", "execute_dax_query", JsonSerializer.Serialize(new { dax = DemoModel.SupportedQuery })) };
                if (QueryBeforeMetadata) calls = results.Length == 0
                    ? [("query", "execute_dax_query", JsonSerializer.Serialize(new { dax = DemoModel.SupportedQuery }))]
                    : [("table-sales", "get_table_details", "{\"table\":\"銷售\"}")];
                if (IncludeUnknownTool && results.Length == 0) calls = [.. calls, ("unsupported", "unsupported_synthetic_tool", "{}")];
                sse = "";
                for (var i = 0; i < calls.Length; i++)
                {
                    var (id, name, arguments) = calls[i]; var split = arguments.Length / 2;
                    sse += Chunk(new { tool_calls = new[] { new { index = i, id, type = "function", function = new { name, arguments = arguments[..split] } } } });
                    sse += Chunk(new { tool_calls = new[] { new { index = i, function = new { arguments = arguments[split..] } } } });
                }
                sse += Chunk(new { }, "tool_calls");
            }
            else sse = Chunk(new { content = marker[..10] }) + Chunk(new { content = marker[10..] }) + Chunk(new { }, "stop");
            return new(HttpStatusCode.OK) { Content = new StringContent(sse + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
        private static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new
        { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";
    }
}
