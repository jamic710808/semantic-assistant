using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class AiStreamingTests
{
    private static readonly ProviderIdentity Profile = new("test", "https://example.invalid/v1/chat/completions", "synthetic-ai");
    private static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new
    { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\r\n\r\n";
    private static string Stop => Chunk(new { }, "stop") + "data: [DONE]\r\n\r\n";
    private static string Calls(params (string Id, string Name, string Args)[] calls) => Chunk(new
    { tool_calls = calls.Select((c, i) => new { index = i, id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Args } }) })
        + Chunk(new { }, "tool_calls") + "data: [DONE]\r\n\r\n";

    private static async Task<List<ProviderEvent>> Parse(string s)
    {
        using var stream = new TinyReadStream(Encoding.UTF8.GetBytes(s));
        var events = new List<ProviderEvent>();
        await foreach (var item in ChatStreamParser.ParseAsync(stream)) events.Add(item);
        return events;
    }

    [Fact]
    public async Task Utf8AndArgumentFragmentsAreReassembledBeforeCalls()
    {
        var s = ": heartbeat\r\n\r\n" + Chunk(new { content = "你好🌱" }) +
            Chunk(new { tool_calls = new[] { new { index = 0, id = "a", type = "function", function = new { name = "get_table_details", arguments = "{\"table\":" } } } }) +
            Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\"銷售\"}" } } } }) +
            Chunk(new { }, "tool_calls") + "data: [DONE]\r\n\r\n";
        var result = await Parse(s);
        Assert.Equal("你好🌱", Assert.IsType<ProviderEvent.Text>(result[0]).Value);
        Assert.Equal("銷售", Assert.IsType<ProviderEvent.Call>(result[1]).Value.Arguments.GetProperty("table").GetString());
    }

    [Fact]
    public async Task MultipleToolCallsRemainSeparateAndOrdered()
    {
        var result = await Parse(Calls(("a", "get_table_details", "{\"table\":\"銷售\"}"),
            ("b", "get_table_details", "{\"table\":\"日期\"}")));
        Assert.Equal(new[] { "a", "b" }, result.Cast<ProviderEvent.Call>().Select(c => c.Value.Id));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"table\":")]
    public async Task BadSecondArgumentsNeverYieldFirstCall(string arguments)
    {
        var events = new List<ProviderEvent>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Calls(("a", "get_table_details", "{}"), ("b", "get_table_details", arguments))));
        await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
        { await foreach (var item in ChatStreamParser.ParseAsync(stream)) events.Add(item); });
        Assert.Empty(events);
    }

    [Fact]
    public async Task TruncatedStreamNeverDispatchesCalls()
    {
        var s = Calls(("a", "get_table_details", "{}"));
        await Assert.ThrowsAsync<ProviderProtocolException>(() => Parse(s[..s.IndexOf("data: [DONE]", StringComparison.Ordinal)]));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("function_call")]
    public async Task IncompleteFinishReasonsAreRejected(string finish) =>
        await Assert.ThrowsAsync<ProviderProtocolException>(() => Parse(Chunk(new { }, finish) + "data: [DONE]\n\n"));

    [Fact]
    public async Task DuplicateIdsAreRejected() => await Assert.ThrowsAsync<ProviderProtocolException>(() =>
        Parse(Calls(("same", "x", "{}"), ("same", "y", "{}"))));

    [Fact]
    public async Task OversizedArgumentsAreRejected() => await Assert.ThrowsAsync<ProviderProtocolException>(() =>
        Parse(Calls(("a", "x", new string('x', 32_001)))));

    [Fact]
    public async Task OversizedLineIsRejected() => await Assert.ThrowsAsync<ProviderProtocolException>(() =>
        Parse("data: " + new string('x', 64_001) + "\n\n"));

    [Fact]
    public async Task MultilineSseDataAndBomWork()
    {
        var s = "\uFEFF" + Stop.Replace("\"choices\":", "\n data: \"choices\":", StringComparison.Ordinal)
            .Replace("\n data:", "\ndata:", StringComparison.Ordinal);
        Assert.Empty(await Parse(s));
    }

    [Fact]
    public async Task AlreadyCancelledParserDoesNotRead()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Stop));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in ChatStreamParser.ParseAsync(stream, cts.Token)) { } });
    }

    private static PermissionSession Session(ProviderIdentity? identity = null, DateTimeOffset? expiry = null,
        params EgressCategory[] categories) => new(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
        ImmutableHashSet.Create(Capability.MetadataRead, Capability.DataQuery),
        new(identity ?? Profile, categories.ToImmutableHashSet(), expiry ?? DateTimeOffset.UtcNow.AddMinutes(1)));

    private static async Task<ConversationOutcome> Run(PermissionSession session, CapturingHandler handler, Probe model)
    {
        using var provider = new OpenAiCompatibleProvider(Profile, new FakeCredential(), handler);
        var coordinator = new ConversationCoordinator(model, provider, new ToolDispatcher(model, model, session), session);
        return await coordinator.RunAsync(new(Guid.NewGuid(), "合成訊息", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
    }

    [Fact]
    public async Task MessageOnlyConsentSendsNoMetadataAndHidesAllTools()
    {
        var handler = new CapturingHandler(Stop); var model = new Probe();
        var result = await Run(Session(categories: EgressCategory.UserMessage), handler, model);
        Assert.Equal("ok", result.Code); Assert.Equal(0, model.Reads);
        Assert.DoesNotContain("合成銷售模型", handler.Bodies.Single());
        Assert.False(JsonDocument.Parse(handler.Bodies.Single()).RootElement.TryGetProperty("tools", out _));
        Assert.DoesNotContain("synthetic-secret", handler.Bodies.Single());
        Assert.Equal("Bearer synthetic-secret", handler.Header);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("expired")]
    [InlineData("no-message")]
    public async Task ConsentMismatchIsDeniedBeforeReadingOrHttp(string kind)
    {
        var identity = kind switch
        {
            "endpoint" => Profile with { Endpoint = "https://other.invalid/v1/chat/completions" },
            "model" => Profile with { Model = "other" },
            "provider" => Profile with { Id = "other" },
            _ => Profile
        };
        var session = Session(identity, kind == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            kind == "no-message" ? EgressCategory.Metadata : EgressCategory.UserMessage);
        var handler = new CapturingHandler(Stop); var model = new Probe();
        Assert.Equal("egress_denied", (await Run(session, handler, model)).Code);
        Assert.Empty(handler.Bodies); Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task ForgedQueryWithNoResultEgressDoesNotExecute()
    {
        var handler = new CapturingHandler(Calls(("q", "execute_dax_query", JsonSerializer.Serialize(new { dax = DemoModel.SupportedQuery }))), Stop);
        var model = new Probe();
        var result = await Run(Session(categories: EgressCategory.UserMessage), handler, model);
        Assert.Equal("ok", result.Code); Assert.Equal("egress_denied", Assert.Single(result.ToolResults).Code);
        Assert.Equal(0, model.Queries); Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task CompleteToolBatchIsEncodedAsOneAssistantMessageThenMatchingResults()
    {
        var handler = new CapturingHandler(Calls(("a", "get_table_details", "{\"table\":\"銷售\"}"),
            ("b", "get_table_details", "{\"table\":\"日期\"}")), Stop);
        var result = await Run(Session(categories: [EgressCategory.UserMessage, EgressCategory.Metadata]), handler, new Probe());
        Assert.Equal("ok", result.Code); Assert.Equal(2, result.ToolResults.Length);
        using var body = JsonDocument.Parse(handler.Bodies[1]);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Single(m => m.GetProperty("role").GetString() == "assistant").GetProperty("tool_calls").GetArrayLength());
        Assert.Equal(new[] { "a", "b" }, messages.Where(m => m.GetProperty("role").GetString() == "tool").Select(m => m.GetProperty("tool_call_id").GetString()));
    }

    [Fact]
    public async Task DirectAdapterCallWithoutApplicationLeaseDoesNotSend()
    {
        var handler = new CapturingHandler(Stop);
        using var provider = new OpenAiCompatibleProvider(Profile, new FakeCredential(), handler);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await foreach (var _ in provider.StreamAsync(new("x", ConversationMode.Analyze, "private", [], []), default)) { } });
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task RedirectResponseIsRejectedWithoutExposingBody()
    {
        var handler = new CapturingHandler("private-secret-server-error") { Status = HttpStatusCode.TemporaryRedirect };
        var result = await Run(Session(categories: EgressCategory.UserMessage), handler, new Probe());
        Assert.Equal("provider_error", result.Code); Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RevocationDiscardsReplyAndPreventsSecondRequest()
    {
        var session = Session(categories: [EgressCategory.UserMessage, EgressCategory.Metadata]);
        var handler = new CapturingHandler(Calls(("a", "get_table_details", "{\"table\":\"銷售\"}")));
        handler.AfterRequest = () => session.Update(DemoModel.Snapshot.Identity, ConversationMode.Analyze, ImmutableHashSet<Capability>.Empty);
        var model = new Probe();
        var result = await Run(session, handler, model);
        Assert.Equal("stale_permission", result.Code); Assert.Empty(result.ToolResults); Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualLoopbackHttpStreamsSyntheticToolRoundTrip(bool dashboard)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = Profile with { Endpoint = $"http://127.0.0.1:{port}/v1/chat/completions" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var planArguments = JsonSerializer.Serialize(new { plan = DashboardProposals.PlanPayload(DemoDashboard.Plan) });
        var half = planArguments.Length / 2;
        var fragmentedProposal = Chunk(new { tool_calls = new[] { new { index = 0, id = "proposal", type = "function", function = new { name = "propose_dashboard", arguments = planArguments[..half] } } } }) +
            Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = planArguments[half..] } } } }) + Chunk(new { }, "tool_calls") + "data: [DONE]\r\n\r\n";
        var responses = new[] { dashboard ? fragmentedProposal : Calls(("a", "get_table_details", "{\"table\":\"銷售\"}")), Chunk(new { content = "合成串流成功" }) + Stop };
        var requestBodies = new List<string>();
        var server = Task.Run(async () =>
        {
            foreach (var response in responses)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                var headers = new StringBuilder(); var one = new byte[1];
                while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, timeout.Token) == 0 || headers.Length > 8192) throw new IOException();
                    headers.Append((char)one[0]);
                }
                var length = int.Parse(headers.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token);
                requestBodies.Add(Encoding.UTF8.GetString(body));
                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
                for (var offset = 0; offset < bytes.Length; offset += 2)
                    await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(2, bytes.Length - offset)), timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
        }, timeout.Token);
        var session = Session(profile, categories: [EgressCategory.UserMessage, EgressCategory.Metadata]);
        using var provider = new OpenAiCompatibleProvider(profile, new FakeCredential());
        var model = new Probe();
        var events = new List<ConversationEvent>();
        var coordinator = new ConversationCoordinator(model, provider, new ToolDispatcher(model, model, session), session);
        var result = await coordinator.RunAsync(new(Guid.NewGuid(), "本機合成測試", ConversationMode.Analyze, session.Snapshot),
            e => { events.Add(e); return Task.CompletedTask; }, timeout.Token);
        await server;
        Assert.Equal("ok", result.Code); Assert.Single(result.ToolResults);
        Assert.Contains(events, e => e.Message == "合成串流成功");
        Assert.Equal(0, model.Queries);
        if (dashboard)
        {
            var proposal = DashboardProposals.Accept(result.ToolResults[0], DemoModel.Snapshot);
            Assert.Equal(DemoDashboard.Plan.Title, proposal.Plan.Title);
            using var body = JsonDocument.Parse(requestBodies[0]);
            Assert.Contains(body.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "propose_dashboard");
            Assert.DoesNotContain(body.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "execute_dax_query");
            Assert.False(session.Snapshot.Allows(Capability.DefinitionWrite));
        }
    }

    [Fact]
    public async Task ForgedDashboardProposalWithoutMetadataEgressReadsNothing()
    {
        var handler = new CapturingHandler(Calls(("p", "propose_dashboard", JsonSerializer.Serialize(new { plan = DashboardProposals.PlanPayload(DemoDashboard.Plan) }))), Stop);
        var model = new Probe(); var result = await Run(Session(categories: EgressCategory.UserMessage), handler, model);
        Assert.Equal("ok", result.Code); Assert.Equal("egress_denied", Assert.Single(result.ToolResults).Code);
        Assert.Equal(0, model.Reads); Assert.Equal(0, model.Queries);
    }

    [Theory]
    [InlineData("http://example.com/v1/chat/completions")]
    [InlineData("https://user:password@example.com/v1/chat/completions")]
    [InlineData("https://example.com/v1/chat/completions?key=secret")]
    [InlineData("https://example.com/v1/responses")]
    public void UnsafeOrWrongProtocolEndpointsAreRejected(string endpoint) =>
        Assert.Throws<ArgumentException>(() => OpenAiCompatibleProvider.ValidateIdentity(Profile with { Endpoint = endpoint }));

    private sealed class Probe : ISemanticModelReader, IReadOnlyDaxQueryRunner
    {
        private readonly DemoModel inner = new();
        public int Reads, Queries;
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken token) { Reads++; return inner.ReadSnapshotAsync(token); }
        public Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token)
        { Queries++; return inner.ExecuteAsync(target, dax, limits, token); }
    }
    private sealed class FakeCredential : ICredentialSource
    { public string ReadSecret(ProviderIdentity _) => "synthetic-secret"; }
    private sealed class CapturingHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? Header;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Action? AfterRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token)); Header = request.Headers.Authorization?.ToString();
            AfterRequest?.Invoke();
            return new(Status) { Content = new StringContent(responses[Math.Min(Bodies.Count - 1, responses.Length - 1)], Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class TinyReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }
}
