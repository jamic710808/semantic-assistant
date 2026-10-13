using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.Mcp;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ToolRegistryTests
{
    private static PermissionSession Session(bool grant = true, EgressConsent? egress = null) => new(DemoModel.Snapshot.Identity,
        ConversationMode.Analyze, grant ? [Capability.ExternalTool] : [], egress);
    private static ToolRegistry Registry() => new(ToolDispatcher.Definitions);
    private static ToolCall Call(string name) => new("selected", name, ContractJson.Element(new { message = "合成測試" }));

    [Fact]
    public void DefaultRegistryNeverDiscoversOrGrantsExternalTools()
    {
        var registry = Registry(); Assert.Equal(3, registry.Snapshot.Tools.Length); Assert.All(registry.Snapshot.Tools, t => Assert.Equal(ToolOrigin.BuiltIn, t.Origin));
        Assert.Equal(3, registry.Snapshot.Tools.Select(t => t.ResultFormat).Distinct().Count());
        var session = Session(); var policy = new ToolPolicyEngine(registry, session); Assert.Empty(policy.AllowedTools(session.Snapshot));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new("execute_dax_query", "forged", Capability.MetadataRead, ContractJson.Element(new { }))]));
    }
    [Fact]
    public void SelectionDoesNotGrantAndNewRegistryVersionInvalidatesOldTurn()
    {
        var registry = Registry(); var session = Session(false); var old = new ToolPolicyEngine(registry, session);
        var binding = new Binding(); registry.ImportAfterUserSelection(binding);
        Assert.Equal(ToolResultFormat.McpDataV1, binding.Descriptor.ResultFormat);
        Assert.Equal("tool_registry_changed", Assert.Throws<ToolPolicyException>(() => old.EnsureCurrent(session.Snapshot)).Code);
        var current = new ToolPolicyEngine(registry, session); Assert.Empty(current.AllowedTools(session.Snapshot));
        session.Update(session.Snapshot.Target, session.Snapshot.Mode, [Capability.ExternalTool]);
        Assert.Single(current.AllowedTools(session.Snapshot)); Assert.False(session.Snapshot.Allows(Capability.DefinitionWrite));
    }
    [Theory]
    [InlineData("capability")]
    [InlineData("origin")]
    [InlineData("risk")]
    [InlineData("target")]
    [InlineData("name")]
    [InlineData("schema")]
    [InlineData("binding")]
    [InlineData("hash")]
    public void ForgedRegistrationsCannotEnterRegistry(string kind)
    {
        var binding = new Binding(); var d = binding.Descriptor;
        binding.Descriptor = kind switch
        {
            "capability" => d with { Definition = d.Definition with { RequiredCapability = Capability.DefinitionWrite } },
            "origin" => d with { Origin = ToolOrigin.BuiltIn },
            "risk" => d with { Risk = ToolRisk.ReadOnlyModel },
            "target" => d with { Target = null },
            "name" => d with { Definition = d.Definition with { Name = "execute_dax_query" } },
            "schema" => d with { Definition = d.Definition with { Parameters = ContractJson.Element("schema") } },
            "binding" => d with { Binding = null },
            _ => d with { Source = d.Source with { DefinitionHash = "bad" } }
        };
        var registry = Registry(); Assert.Throws<ToolPolicyException>(() => registry.ImportAfterUserSelection(binding)); Assert.Equal(3, registry.Snapshot.Tools.Length);
    }
    [Fact]
    public void DuplicateAndMaximumEightSelectionsAreBounded()
    {
        var registry = Registry(); var first = new Binding(); registry.ImportAfterUserSelection(first);
        Assert.Throws<ToolPolicyException>(() => registry.ImportAfterUserSelection(first));
        for (var i = 0; i < 7; i++) registry.ImportAfterUserSelection(new Binding());
        Assert.Throws<ToolPolicyException>(() => registry.ImportAfterUserSelection(new Binding())); Assert.Equal(8, registry.ClearExternal().Length); Assert.Equal(3, registry.Snapshot.Tools.Length);
    }
    [Fact]
    public async Task StaleSourceSessionAndMissingConfirmationDoNotInvokeAdapter()
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var session = Session(); var model = new Probe();
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry);
        Assert.Equal("approval_required", (await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default)).Code);
        var old = session.Snapshot; session.Update(old.Target with { Revision = "changed" }, old.Mode, old.Granted);
        Assert.Equal("stale_permission", (await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), old, default)).Code);
        Assert.Equal("stale_external_tool", (await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default)).Code);
        Assert.Equal(0, binding.Calls); Assert.Equal(0, model.Reads); Assert.Equal(0, model.Queries);
    }
    [Fact]
    public async Task PerCallRefusalDoesNotExecuteAndSuccessfulCallHasExternalCategory()
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var session = Session(); var model = new Probe();
        var denied = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(false));
        Assert.Equal("external_denied", (await denied.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default)).Code); Assert.Equal(0, binding.Calls);
        var allowed = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(true));
        var result = await allowed.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default);
        Assert.True(result.Success); Assert.Equal(EgressCategory.ExternalToolData, result.PayloadCategory); Assert.Equal(1, binding.Calls); Assert.Equal(0, model.Reads);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeOrRegistryChangeDuringConfirmationPreventsSideEffect(bool registryChange)
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var session = Session(); var model = new Probe();
        var modes = new ModeController(AssistantMode.Analyze, session.Snapshot.Target); var state = modes.Snapshot;
        var dispatcher = new ToolDispatcher(model, model, session, modes: modes, modeSnapshot: state, toolRegistry: registry, externalConfirmation: (_, _) =>
        { if (registryChange) registry.ClearExternal(); else modes.Change(AssistantMode.Report, state.Target); return Task.FromResult(true); });
        var result = await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default);
        Assert.Equal(registryChange ? "tool_registry_changed" : "invalid_mode", result.Code); Assert.Equal(0, binding.Calls); Assert.Null(result.Payload);
    }
    [Fact]
    public async Task ChangedCatalogOrForgedResultNeverDeliversPayload()
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var session = Session(); var model = new Probe();
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(true));
        binding.AfterCall = () => binding.Current = false;
        var stale = await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default); Assert.Equal("stale_external_tool", stale.Code); Assert.Null(stale.Payload);
        binding.Current = true; binding.AfterCall = null; binding.ForgeResult = true;
        var forged = await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default); Assert.Equal("invalid_provenance", forged.Code); Assert.Null(forged.Payload);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalDataRequiresItsOwnEgressNotMetadata(bool grantExternal)
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var profile = new ProviderIdentity("fixture", "https://example.invalid/v1/chat/completions", "synthetic");
        var categories = ImmutableHashSet.Create(EgressCategory.UserMessage, grantExternal ? EgressCategory.ExternalToolData : EgressCategory.Metadata);
        var session = Session(egress: new(profile, categories, DateTimeOffset.UtcNow.AddMinutes(1))); var model = new Probe();
        var provider = new Provider(binding.Descriptor.Definition.Name, profile); var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(true));
        var result = await new ConversationCoordinator(model, provider, dispatcher, session).RunAsync(new(Guid.NewGuid(), "合成", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
        Assert.Equal(grantExternal ? "ok" : "egress_denied", result.Code); Assert.Equal(grantExternal ? 1 : 0, binding.Calls);
        Assert.Equal(grantExternal, provider.Requests[0].Tools.Any(t => t.RequiredCapability == Capability.ExternalTool)); Assert.Equal(0, model.Reads);
    }
    [Fact]
    public async Task StreamInvalidationStopsNextRoundAndClearsPreviousResults()
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var session = Session(); var model = new Probe();
        var provider = new Provider(binding.Descriptor.Definition.Name) { BeforeFinal = () => registry.ClearExternal() };
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(true));
        var result = await new ConversationCoordinator(model, provider, dispatcher, session).RunAsync(new(Guid.NewGuid(), "合成", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
        Assert.Equal("tool_registry_changed", result.Code); Assert.Empty(result.ToolResults); Assert.Equal(2, provider.Requests.Count);
    }
    [Fact]
    public void ExternalResultCloneDoesNotRenewFiveMinuteLifetime()
    {
        var result = new ToolResult("a", "mcp_fixture", true, "ok", "fixed", ContractJson.Element(new { value = 1 })) { PayloadCategory = EgressCategory.ExternalToolData };
        var copy = result with { Summary = "copy" }; Assert.Equal(result.ReceivedAt, copy.ReceivedAt); Assert.Equal(result.ReceivedTimestamp, copy.ReceivedTimestamp);
        Assert.Throws<QueryResultExpiredException>(() => ToolPolicyEngine.EnsureResultFresh(copy, new FutureClock(result.ReceivedAt.AddMinutes(6))));
        Assert.DoesNotContain("ReceivedTimestamp", JsonSerializer.Serialize(copy));
    }
    [Fact]
    public async Task ActualMcpSchemaValidationPrecedesConfirmationAndRefusalDoesNotCall()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(), default); var tool = Assert.Single(await client.ListToolsAsync(default));
        var binding = new McpAiToolBinding(client, tool, DemoModel.Snapshot.Identity); var registry = Registry(); registry.ImportAfterUserSelection(binding);
        var session = Session(); var model = new Probe(); var confirmations = 0;
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => { confirmations++; return Task.FromResult(true); });
        var invalid = await dispatcher.DispatchAsync(new("bad", binding.Descriptor.Definition.Name, ContractJson.Element(new { message = 4 })), session.Snapshot, default);
        Assert.False(invalid.Success); Assert.Equal(0, confirmations);
        var denied = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(false));
        Assert.Equal("external_denied", (await denied.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default)).Code);
        var result = await dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, default);
        Assert.True(result.Success); Assert.Equal(1, confirmations); Assert.Equal(1, result.Payload!.Value.GetProperty("StructuredContent").GetProperty("Calls").GetInt32());
    }
    [Fact]
    public async Task ActualCatalogNotificationStopsAiTurnWithoutRetryOrPayload()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration("change"), default); var tool = Assert.Single(await client.ListToolsAsync(default));
        var binding = new McpAiToolBinding(client, tool, DemoModel.Snapshot.Identity); var registry = Registry(); registry.ImportAfterUserSelection(binding);
        var session = Session(); var model = new Probe(); var provider = new Provider(binding.Descriptor.Definition.Name);
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => Task.FromResult(true));
        var result = await new ConversationCoordinator(model, provider, dispatcher, session).RunAsync(new(Guid.NewGuid(), "合成", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
        Assert.Equal("stale_external_tool", result.Code); Assert.Empty(result.ToolResults); Assert.Single(provider.Requests);
    }
    [Fact]
    public async Task ActualLoopbackHttpAndMcpChildCompleteTwoRoundTripWithSeparateConsent()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(), default); var tool = Assert.Single(await client.ListToolsAsync(default));
        var binding = new McpAiToolBinding(client, tool, DemoModel.Snapshot.Identity); var registry = Registry(); registry.ImportAfterUserSelection(binding);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = new ProviderIdentity("loopback", $"http://127.0.0.1:{port}/v1/chat/completions", "synthetic");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var bodies = new List<string>();
        var first = Chunk(new { tool_calls = new[] { new { index = 0, id = "external", type = "function", function = new { name = binding.Descriptor.Definition.Name, arguments = "{\"message\":\"HTTP合成\"}" } } } }, "tool_calls");
        var server = Task.Run(async () =>
        {
            foreach (var response in new[] { first, Chunk(new { content = "合成外部操作完成" }, "stop") })
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token); await using var stream = connection.GetStream(); var headers = new StringBuilder(); var one = new byte[1];
                while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                { if (await stream.ReadAsync(one, timeout.Token) == 0 || headers.Length > 8192) throw new IOException(); headers.Append((char)one[0]); }
                var length = int.Parse(headers.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                if (length is < 1 or > 512000) throw new IOException(); var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token); bodies.Add(Encoding.UTF8.GetString(body));
                var bytes = Encoding.UTF8.GetBytes(response); await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
                for (var offset = 0; offset < bytes.Length; offset += 2) await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(2, bytes.Length - offset)), timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
        }, timeout.Token);
        var session = Session(egress: new(profile, [EgressCategory.UserMessage, EgressCategory.ExternalToolData], DateTimeOffset.UtcNow.AddMinutes(1)));
        var model = new Probe(); var confirmations = 0; using var provider = new OpenAiCompatibleProvider(profile, new NoCredentials());
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (preview, _) =>
        { Assert.Equal("synthetic.echo", preview.OriginalName); Assert.Equal(binding.Descriptor.Source, preview.Source); confirmations++; return Task.FromResult(true); });
        var result = await new ConversationCoordinator(model, provider, dispatcher, session).RunAsync(new(Guid.NewGuid(), "合成HTTP操作", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask, timeout.Token);
        await server; Assert.Equal("ok", result.Code); Assert.Equal(1, confirmations); Assert.Single(result.ToolResults); Assert.Equal(0, model.Reads); Assert.Equal(0, model.Queries);
        Assert.Equal(2, bodies.Count); using var request = JsonDocument.Parse(bodies[0]); Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
        foreach (var body in bodies) { Assert.DoesNotContain(client.Source, body); Assert.DoesNotContain("DOTNET", body); Assert.DoesNotContain("銷售額", body); }
        using var next = JsonDocument.Parse(bodies[1]); var content = next.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
        using var envelope = JsonDocument.Parse(content); Assert.Equal(1, envelope.RootElement.GetProperty("Payload").GetProperty("StructuredContent").GetProperty("Calls").GetInt32());
    }
    private static string Chunk(object delta, string finish) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\r\n\r\ndata: [DONE]\r\n\r\n";
    [Theory]
    [InlineData("definition")]
    [InlineData("call-only")]
    [InlineData("misclassified-payload")]
    [InlineData("message-category")]
    [InlineData("native-as-external")]
    public async Task DirectProviderBoundaryRejectsExternalOrMisclassifiedDataBeforeHttpAndSecretRead(string kind)
    {
        var profile = new ProviderIdentity("fixture", "https://example.invalid/v1/chat/completions", "synthetic");
        var session = Session(false, new(profile, [EgressCategory.UserMessage,
            kind == "native-as-external" ? EgressCategory.ExternalToolData : EgressCategory.Metadata], DateTimeOffset.UtcNow.AddMinutes(1)));
        var capture = new CaptureLease(profile); var model = new Probe();
        await new ConversationCoordinator(model, capture, new ToolDispatcher(model, model, session), session).RunAsync(new(Guid.NewGuid(), "合成", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
        var request = capture.Request!;
        request = kind switch
        {
            "definition" => request with { Tools = [new("mcp_fixture", "external", Capability.ExternalTool, ContractJson.Element(new { type = "object" }))] },
            "call-only" => request with { Messages = [new("assistant", "", Call: Call("mcp_fixture"))] },
            "misclassified-payload" => request with { Messages = [new("tool", "", Result: new("a", "mcp_fixture", true, "ok", "fixed", ContractJson.Element(new { privateValue = 123 })) { PayloadCategory = EgressCategory.Metadata })] },
            "native-as-external" => request with { Messages = [new("tool", "", Result: new("a", "get_table_details", true, "ok", "fixed", ContractJson.Element(new { privateValue = 123 })) { PayloadCategory = EgressCategory.ExternalToolData })] },
            _ => request with { Messages = [new("tool", "", Result: new("a", "get_table_details", true, "ok", "fixed", ContractJson.Element(new { privateValue = 123 })) { PayloadCategory = EgressCategory.UserMessage })] }
        };
        var credentials = new CountingCredentials(); var handler = new CountingHandler(); using var provider = new OpenAiCompatibleProvider(profile, credentials, handler);
        await Assert.ThrowsAsync<EgressAuthorizationException>(async () => { await foreach (var _ in provider.StreamAsync(request, default)) { } });
        Assert.Equal(0, handler.Calls); Assert.Equal(0, credentials.Reads);
    }
    [Fact]
    public async Task ExpiredExternalPayloadCannotReachHttpEvenWithConsent()
    {
        var profile = new ProviderIdentity("fixture", "https://example.invalid/v1/chat/completions", "synthetic");
        var session = Session(false, new(profile, [EgressCategory.UserMessage, EgressCategory.ExternalToolData], DateTimeOffset.UtcNow.AddMinutes(1)));
        var capture = new CaptureLease(profile); var model = new Probe();
        await new ConversationCoordinator(model, capture, new ToolDispatcher(model, model, session), session).RunAsync(new(Guid.NewGuid(), "合成", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
        var result = new ToolResult("a", "mcp_fixture", true, "ok", "fixed", ContractJson.Element(new { fixture = 1 })) { PayloadCategory = EgressCategory.ExternalToolData };
        var request = capture.Request! with { Messages = [new("tool", "", Result: result)] }; var credentials = new CountingCredentials(); var handler = new CountingHandler();
        using var provider = new OpenAiCompatibleProvider(profile, credentials, handler, new FutureClock(result.ReceivedAt.AddMinutes(6)));
        await Assert.ThrowsAsync<QueryResultExpiredException>(async () => { await foreach (var _ in provider.StreamAsync(request, default)) { } });
        Assert.Equal(0, handler.Calls); Assert.Equal(0, credentials.Reads);
    }
    [Fact]
    public async Task CancellationDuringLateConfirmationCannotExecuteAfterCallerStopsWaiting()
    {
        var registry = Registry(); var binding = new Binding(); registry.ImportAfterUserSelection(binding); var session = Session(); var model = new Probe();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var token = new CancellationTokenSource();
        var dispatcher = new ToolDispatcher(model, model, session, toolRegistry: registry, externalConfirmation: (_, _) => { entered.TrySetResult(); return confirmation.Task; });
        var run = dispatcher.DispatchAsync(Call(binding.Descriptor.Definition.Name), session.Snapshot, token.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); confirmation.SetResult(true);
        await binding.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(0, binding.Calls);
    }
    private sealed class CaptureLease(ProviderIdentity identity) : IAiProvider
    {
        public bool UsesExternalNetwork => true; public ProviderIdentity? Identity => identity; public ProviderRequest? Request;
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request, [EnumeratorCancellation] CancellationToken token)
        { Request = request; await Task.Yield(); token.ThrowIfCancellationRequested(); yield return new ProviderEvent.Text("fixed"); }
    }
    private sealed class CountingCredentials : ICredentialSource
    { public int Reads; public string? ReadSecret(ProviderIdentity provider) { Reads++; return null; } }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No HTTP expected."); }
    }
    private static McpProcessConfiguration Configuration(string mode = "counting")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticAssistant.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new IOException(); var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return new(host, [Path.Combine(root, "tests", "SemanticAssistant.FixtureHost", "bin", "Release", "net10.0", "SemanticAssistant.FixtureHost.dll"), mode]);
    }
    private sealed class NoCredentials : ICredentialSource { public string? ReadSecret(ProviderIdentity provider) => null; }
    private sealed class FutureClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Probe : ISemanticModelReader, IReadOnlyDaxQueryRunner
    {
        public int Reads, Queries;
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken token) { Reads++; return Task.FromResult(DemoModel.Snapshot); }
        public Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token) { Queries++; throw new InvalidOperationException(); }
    }
    private sealed class Binding : IExternalToolBinding
    {
        public RegisteredTool Descriptor { get; set; }
        public bool Current = true; public bool IsCurrent => Current; public int Calls; public bool ForgeResult; public Action? AfterCall;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Binding() => Descriptor = new(new("mcp_" + Guid.NewGuid().ToString("N"), "合成工具", Capability.ExternalTool, ContractJson.Element(new { type = "object" })),
            ToolOrigin.McpProcess, ToolRisk.UnknownExternalEffects, new("fixture", "自製程序", 1, new string('a', 64)), DemoModel.Snapshot.Identity, this);
        public async Task<ToolResult> ExecuteAsync(ToolCall call, PermissionSession session, PermissionSnapshot permissions,
            Func<ExternalToolPreview, CancellationToken, Task<bool>> confirmation, Action guard, CancellationToken token)
        {
            try
            {
                guard(); if (!await confirmation(new(Descriptor.Source, call.Name, "fixture", call.Arguments), token)) return new(call.Id, call.Name, false, "external_denied", "fixed");
                guard(); Calls++; AfterCall?.Invoke();
                return new(call.Id, call.Name, true, "ok", "fixed", ContractJson.Element(new { fixture = true }))
                { PayloadCategory = ForgeResult ? EgressCategory.Metadata : EgressCategory.ExternalToolData };
            }
            finally { Finished.TrySetResult(); }
        }
        public ValueTask DisposeAsync() { Current = false; return ValueTask.CompletedTask; }
    }
    private sealed class Provider(string alias, ProviderIdentity? identity = null) : IAiProvider
    {
        public bool UsesExternalNetwork => identity is not null; public ProviderIdentity? Identity => identity; public Action? BeforeFinal; public List<ProviderRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request); await Task.Yield(); token.ThrowIfCancellationRequested();
            if (request.Messages.IsEmpty) yield return new ProviderEvent.Call(Call(alias));
            else { BeforeFinal?.Invoke(); yield return new ProviderEvent.Text("fixed"); }
        }
    }
}
