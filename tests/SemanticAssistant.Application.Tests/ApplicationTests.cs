using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ApplicationTests
{
    private static PermissionSession Session(params Capability[] grants) =>
        new(DemoModel.Snapshot.Identity, ConversationMode.Analyze, grants.ToImmutableHashSet());

    private static ToolCall Query(string id = "query-1") =>
        new(id, "execute_dax_query", ContractJson.Element(new { dax = DemoModel.SupportedQuery }));

    [Fact]
    public async Task ForgedQueryIsDeniedBeforeAdapterExecutes()
    {
        var model = new ProbeModel();
        var session = Session(Capability.MetadataRead);
        var dispatcher = new ToolDispatcher(model, model, session);
        Assert.DoesNotContain(dispatcher.AllowedTools(session.Snapshot), t => t.Name == "execute_dax_query");
        var result = await dispatcher.DispatchAsync(Query(), session.Snapshot, default);
        Assert.Equal("permission_denied", result.Code);
        Assert.Equal(0, model.Reads);
        Assert.Equal(0, model.Queries);
    }

    [Fact]
    public async Task UnknownToolFailsClosed()
    {
        var model = new DemoModel();
        var session = Session(Capability.MetadataRead, Capability.DataQuery);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(
            new("1", "delete_table", ContractJson.Element(new { table = "銷售" })), session.Snapshot, default);
        Assert.Equal("unknown_tool", result.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"table\":4}")]
    [InlineData("{\"table\":\"銷售\",\"path\":\"C:/\"}")]
    [InlineData("{\"table\":\"銷售\",\"table\":\"日期\"}")]
    [InlineData("{\"table\":\" \"}")]
    [InlineData("[]")]
    public async Task InvalidArgumentsAreRejected(string json)
    {
        var model = new ProbeModel();
        var session = Session(Capability.MetadataRead);
        using var document = JsonDocument.Parse(json);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(
            new("1", "get_table_details", document.RootElement), session.Snapshot, default);
        Assert.Equal("invalid_arguments", result.Code);
        Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task SchemaAndValidatorAgreeOnLength()
    {
        var definition = ToolDispatcher.Definitions.Single(t => t.Name == "get_table_details");
        Assert.False(definition.Parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(128, definition.Parameters.GetProperty("properties").GetProperty("table").GetProperty("maxLength").GetInt32());
        var model = new DemoModel();
        var session = Session(Capability.MetadataRead);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(
            new("1", definition.Name, ContractJson.Element(new { table = new string('x', 129) })), session.Snapshot, default);
        Assert.Equal("invalid_arguments", result.Code);
    }

    [Fact]
    public async Task ChangedTargetRevisionIsRejected()
    {
        var model = new ProbeModel { TargetChanged = true };
        var session = Session(Capability.DataQuery);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Query(), session.Snapshot, default);
        Assert.Equal("target_changed", result.Code);
        Assert.Equal(0, model.Queries);
    }

    [Fact]
    public async Task RevokedSnapshotIsRejected()
    {
        var model = new ProbeModel();
        var session = Session(Capability.DataQuery);
        var prior = session.Snapshot;
        session.Update(prior.Target, prior.Mode, ImmutableHashSet<Capability>.Empty);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Query(), prior, default);
        Assert.Equal("stale_permission", result.Code);
        Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task LateResultAfterRevocationIsDiscarded()
    {
        var model = new ProbeModel();
        var session = Session(Capability.DataQuery);
        var prior = session.Snapshot;
        model.AfterQuery = () => session.Update(prior.Target, prior.Mode, ImmutableHashSet<Capability>.Empty);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Query(), prior, default);
        Assert.Equal("stale_permission", result.Code);
        Assert.Null(result.Query);
    }

    [Fact]
    public async Task QueryTimeoutReturnsNoRows()
    {
        var model = new ProbeModel { DelayQuery = true };
        var session = Session(Capability.DataQuery);
        var result = await new ToolDispatcher(model, model, session, new(Timeout: TimeSpan.FromMilliseconds(30)))
            .DispatchAsync(Query(), session.Snapshot, default);
        Assert.Equal("query_timeout", result.Code);
        Assert.Null(result.Query);
    }

    [Fact]
    public async Task ResultByteLimitIsEnforced()
    {
        var model = new DemoModel();
        var session = Session(Capability.DataQuery);
        var result = await new ToolDispatcher(model, model, session, new(MaxBytes: 5)).DispatchAsync(Query(), session.Snapshot, default);
        Assert.Equal("result_limit", result.Code);
        Assert.Null(result.Query);
    }

    [Fact]
    public async Task ForgedResultProvenanceIsRejected()
    {
        var model = new ProbeModel { InvalidSource = true };
        var session = Session(Capability.DataQuery);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Query(), session.Snapshot, default);
        Assert.Equal("invalid_provenance", result.Code);
    }

    [Fact]
    public async Task DemoCompletesRealToolLoopAndVersionedEvents()
    {
        var model = new DemoModel();
        var session = Session(Capability.MetadataRead, Capability.DataQuery);
        var events = new List<ConversationEvent>();
        var request = new ConversationRequest(Guid.NewGuid(), "分析數值", ConversationMode.Analyze, session.Snapshot);
        var result = await new ConversationCoordinator(model, new DemoAiProvider(), new(model, model, session), session)
            .RunAsync(request, e => { events.Add(e); return Task.CompletedTask; });
        Assert.Equal("ok", result.Code);
        Assert.Equal(2, result.ToolResults.Length);
        Assert.Equal(3, result.ToolResults[1].Query!.Rows.Length);
        Assert.All(events, e => { Assert.Equal(1, e.Version); Assert.Equal(request.RequestId, e.RequestId); });
        Assert.Equal(Enumerable.Range(1, events.Count).Select(x => (long)x), events.Select(e => e.Sequence));
        Assert.Equal(EventKind.Completed, events[^1].Kind);
        Assert.DoesNotContain(events, e => e.Message.Contains("120000"));
    }

    [Fact]
    public async Task NoMetadataPermissionDoesNotReadContext()
    {
        var model = new ProbeModel();
        var session = Session();
        await Run(model, new ScriptProvider(), session);
        Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task ExternalProviderCannotReceiveContextInM1()
    {
        var model = new ProbeModel();
        var provider = new ScriptProvider { UsesExternalNetwork = true };
        var result = await Run(model, provider, Session(Capability.MetadataRead));
        Assert.Equal("external_provider_disabled", result.Code);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task DuplicateCallIdIsNotExecutedTwice()
    {
        var model = new ProbeModel();
        var provider = new ScriptProvider { RepeatCall = true };
        var result = await Run(model, provider, Session(Capability.DataQuery));
        Assert.Equal("invalid_call_id", result.Code);
        Assert.Equal(1, model.Queries);
    }

    [Fact]
    public async Task EndlessToolLoopStopsAtRoundBudget()
    {
        var result = await Run(new ProbeModel(), new ScriptProvider { Loop = true }, Session(Capability.DataQuery), new(MaxRounds: 2));
        Assert.Equal("round_limit", result.Code);
        Assert.Equal(2, result.ToolResults.Length);
    }

    [Fact]
    public async Task MultipleCallsStopAtToolBudget()
    {
        var model = new ProbeModel();
        var result = await Run(model, new ScriptProvider { TwoCalls = true }, Session(Capability.DataQuery), new(MaxToolCalls: 1));
        Assert.Equal("tool_limit", result.Code);
        Assert.Equal(1, model.Queries);
    }

    [Fact]
    public async Task TextBudgetIsEnforced()
    {
        var result = await Run(new ProbeModel(), new ScriptProvider { Output = "12345" }, Session(), new(MaxTextCharacters: 4));
        Assert.Equal("text_limit", result.Code);
    }

    [Fact]
    public async Task CancellationStopsBeforeAnyToolExecutes()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new ProbeModel();
        var provider = new ScriptProvider { BeforeOutput = () => cancellation.Cancel(), TwoCalls = true };
        var result = await Run(model, provider, Session(Capability.DataQuery), cancellationToken: cancellation.Token);
        Assert.Equal("cancelled", result.Code);
        Assert.Equal(0, model.Queries);
    }

    [Fact]
    public async Task LateTextAfterCancellationIsNotEmitted()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new ProbeModel();
        var session = Session();
        var events = new List<ConversationEvent>();
        var provider = new ScriptProvider { BeforeOutput = () => cancellation.Cancel(), Output = "late-text" };
        var result = await new ConversationCoordinator(model, provider, new(model, model, session), session).RunAsync(
            new(Guid.NewGuid(), "x", ConversationMode.Analyze, session.Snapshot), e => { events.Add(e); return Task.CompletedTask; }, cancellation.Token);
        Assert.Equal("cancelled", result.Code);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.Text);
    }

    [Fact]
    public async Task ProviderReplyAfterRevocationIsNotEmitted()
    {
        var model = new ProbeModel();
        var session = Session();
        var prior = session.Snapshot;
        var events = new List<ConversationEvent>();
        var provider = new ScriptProvider { BeforeOutput = () => session.Update(prior.Target, prior.Mode, prior.Granted), Output = "late-text" };
        var result = await new ConversationCoordinator(model, provider, new(model, model, session), session).RunAsync(
            new(Guid.NewGuid(), "x", ConversationMode.Analyze, prior), e => { events.Add(e); return Task.CompletedTask; });
        Assert.Equal("stale_permission", result.Code);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.Text);
    }

    [Fact]
    public async Task TotalTurnTimeoutStopsSlowProvider()
    {
        var result = await Run(new ProbeModel(), new ScriptProvider { Delay = true }, Session(), new(Timeout: TimeSpan.FromMilliseconds(30)));
        Assert.Equal("turn_timeout", result.Code);
    }

    [Fact]
    public async Task NonCooperativeProviderDoesNotPublishLateEventsOrDispatchTools()
    {
        var model = new ProbeModel();
        var provider = new ScriptProvider { IgnoreCancellation = true, TwoCalls = true };
        var session = Session(Capability.DataQuery);
        var events = new List<ConversationEvent>();
        var result = await new ConversationCoordinator(model, provider, new(model, model, session), session,
            new(Timeout: TimeSpan.FromMilliseconds(30))).RunAsync(new(Guid.NewGuid(), "x", ConversationMode.Analyze, session.Snapshot),
                e => { events.Add(e); return Task.CompletedTask; });
        Assert.Equal("turn_timeout", result.Code);
        var count = events.Count;
        await Task.Delay(300);
        Assert.Equal(count, events.Count);
        Assert.Equal(0, model.Queries);
    }

    [Fact]
    public async Task ElapsedDeadlineRejectsProviderCallsBeforeCancellationTimerRuns()
    {
        var model = new ProbeModel();
        var clock = new ManualTurnClock();
        var provider = new ScriptProvider { TwoCalls = true, BeforeOutput = () => clock.Advance(TimeSpan.FromHours(2)) };
        var session = Session(Capability.DataQuery);
        var events = new List<ConversationEvent>();
        var result = await new ConversationCoordinator(model, provider, new(model, model, session), session,
            new(Timeout: TimeSpan.FromMinutes(1)), turnClock: clock).RunAsync(
                new(Guid.NewGuid(), "x", ConversationMode.Analyze, session.Snapshot),
                e => { events.Add(e); return Task.CompletedTask; });
        Assert.Equal("turn_timeout", result.Code);
        Assert.Equal(0, model.Queries);
        Assert.DoesNotContain(events, e => e.Kind is EventKind.Text or EventKind.ToolStarted or EventKind.ToolCompleted);
    }

    [Fact]
    public async Task ElapsedDeadlineDuringToolPreviewPreventsDispatch()
    {
        var model = new ProbeModel();
        var clock = new ManualTurnClock();
        var session = Session(Capability.DataQuery);
        var events = new List<ConversationEvent>();
        var result = await new ConversationCoordinator(model, new ScriptProvider { TwoCalls = true },
            new(model, model, session), session, new(Timeout: TimeSpan.FromMinutes(1)), turnClock: clock).RunAsync(
                new(Guid.NewGuid(), "x", ConversationMode.Analyze, session.Snapshot), e =>
                {
                    events.Add(e);
                    if (e.Kind == EventKind.ToolStarted) clock.Advance(TimeSpan.FromHours(2));
                    return Task.CompletedTask;
                });
        Assert.Equal("turn_timeout", result.Code);
        Assert.Equal(0, model.Queries);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.ToolCompleted);
    }

    [Fact]
    public async Task ModeMismatchFailsClosed()
    {
        var model = new ProbeModel();
        var session = Session(Capability.MetadataRead);
        var result = await new ConversationCoordinator(model, new ScriptProvider(), new(model, model, session), session)
            .RunAsync(new(Guid.NewGuid(), "x", ConversationMode.Report, session.Snapshot), _ => Task.CompletedTask);
        Assert.Equal("stale_permission", result.Code);
        Assert.Equal(0, model.Reads);
    }

    [Fact]
    public async Task ExceptionsDoNotExposeConnectionSecrets()
    {
        var result = await Run(new ProbeModel(), new ScriptProvider { Fail = true }, Session());
        Assert.Equal("provider_error", result.Code);
    }

    [Fact]
    public void RendererEscapesTextAndRetainsSource()
    {
        var result = new QueryResult("test-result", DemoModel.Snapshot.Identity, ["<script>alert(1)</script>"],
            [[ContractJson.Element("<img src=x onerror=alert(1)>")]], false, "EVALUATE <unsafe>");
        var html = HtmlReportRenderer.Render(result);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("test-result", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("合成示範資料", html);
    }

    [Fact]
    public async Task ArtifactExportRequiresSeparateCapability()
    {
        var session = Session(Capability.DataQuery);
        var folder = Path.Combine(Path.GetTempPath(), "SemanticAssistant-synthetic-tests", Guid.NewGuid().ToString("N"));
        var result = await new DemoModel().ExecuteAsync(session.Snapshot.Target, DemoModel.SupportedQuery, new(), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new HtmlReportExporter(folder, session).ExportAsync(result, session.Snapshot, default));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task AuthorizedArtifactHasTraceableSyntheticSource()
    {
        var session = Session(Capability.ArtifactWrite);
        var folder = Path.Combine(Path.GetTempPath(), "SemanticAssistant-synthetic-tests", Guid.NewGuid().ToString("N"));
        var result = await new DemoModel().ExecuteAsync(session.Snapshot.Target, DemoModel.SupportedQuery, new(), default);
        var path = await new HtmlReportExporter(folder, session).ExportAsync(result, session.Snapshot, default);
        Assert.Equal(folder, Path.GetDirectoryName(path));
        Assert.Contains(result.ResultId, await File.ReadAllTextAsync(path));
        Assert.Contains("合成示範資料", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ArtifactFromWrongModelIsRejected()
    {
        var session = Session(Capability.ArtifactWrite);
        var result = await new DemoModel().ExecuteAsync(session.Snapshot.Target, DemoModel.SupportedQuery, new(), default);
        var folder = Path.Combine(Path.GetTempPath(), "SemanticAssistant-synthetic-tests", Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new HtmlReportExporter(folder, session)
            .ExportAsync(result with { Source = result.Source with { Id = "forged" } }, session.Snapshot, default));
        Assert.False(Directory.Exists(folder));
    }

    private static Task<ConversationOutcome> Run(ProbeModel model, IAiProvider provider, PermissionSession session,
        TurnLimits? limits = null, CancellationToken cancellationToken = default) =>
        new ConversationCoordinator(model, provider, new(model, model, session), session, limits).RunAsync(
            new(Guid.NewGuid(), "分析數值", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask, cancellationToken);

    private sealed class ManualTurnClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    private sealed class ProbeModel : ISemanticModelReader, IReadOnlyDaxQueryRunner
    {
        public int Reads { get; private set; }
        public int Queries { get; private set; }
        public bool TargetChanged { get; init; }
        public bool InvalidSource { get; init; }
        public bool DelayQuery { get; init; }
        public Action? AfterQuery { get; set; }
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(TargetChanged ? DemoModel.Snapshot with { Identity = DemoModel.Snapshot.Identity with { Revision = "changed" } } : DemoModel.Snapshot);
        }
        public async Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken cancellationToken)
        {
            Queries++;
            if (DelayQuery) await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var result = await new DemoModel().ExecuteAsync(target, dax, limits, cancellationToken);
            AfterQuery?.Invoke();
            return InvalidSource ? result with { Source = target with { Id = "other-model" } } : result;
        }
    }

    private sealed class ScriptProvider : IAiProvider
    {
        public bool UsesExternalNetwork { get; init; }
        public bool RepeatCall { get; init; }
        public bool Loop { get; init; }
        public bool TwoCalls { get; init; }
        public bool Delay { get; init; }
        public bool Fail { get; init; }
        public bool IgnoreCancellation { get; init; }
        public string Output { get; init; } = "done";
        public Action? BeforeOutput { get; init; }
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            if (Delay) await Task.Delay(3_000, cancellationToken);
            if (IgnoreCancellation) await Task.Delay(200);
            if (Fail) throw new InvalidOperationException("secret-token-should-never-be-shown");
            BeforeOutput?.Invoke();
            if (RepeatCall || Loop || TwoCalls)
            {
                yield return new ProviderEvent.Call(Query(RepeatCall ? "same-id" : "call-" + Calls));
                if (TwoCalls) yield return new ProviderEvent.Call(Query("second"));
            }
            else yield return new ProviderEvent.Text(Output);
        }
    }
}
