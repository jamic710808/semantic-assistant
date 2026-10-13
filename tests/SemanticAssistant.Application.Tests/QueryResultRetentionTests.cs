using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class QueryResultRetentionTests
{
    private static Task<QueryResult> Result() => new DemoModel().ExecuteAsync(DemoModel.Snapshot.Identity, DemoModel.SupportedQuery, new(), default);

    [Fact]
    public async Task CloningCannotRenewLifetimeAndTimestampIsNotSerialized()
    {
        var original = await Result(); var clone = original with { ResultId = "synthetic-clone" };
        Assert.Equal(original.ReceivedAt, clone.ReceivedAt);
        Assert.Equal(original.ReceivedTimestamp, clone.ReceivedTimestamp);
        Assert.Equal(TimeSpan.FromMinutes(5), original.ExpiresAt - original.ReceivedAt);
        Assert.True(QueryResultRetention.IsFresh(clone));
        var json = JsonSerializer.Serialize(clone);
        Assert.DoesNotContain("ReceivedTimestamp", json);
        Assert.Contains("ExpiresAt", json);
    }

    [Theory]
    [InlineData(6, 0)]
    [InlineData(-6, 6)]
    [InlineData(0, -6)]
    public async Task WallExpiryMonotonicExpiryAndBackwardTimestampAllReject(int wallMinutes, int timestampMinutes)
    {
        var result = await Result(); var clock = new ShiftClock { WallMinutes = wallMinutes, TimestampMinutes = timestampMinutes };
        Assert.False(QueryResultRetention.IsFresh(result, clock));
        Assert.Equal(TimeSpan.Zero, QueryResultRetention.Remaining(result, clock));
        Assert.Throws<QueryResultExpiredException>(() => QueryResultRetention.EnsureFresh(result, clock));
    }

    [Fact]
    public async Task BackwardUtcCannotIncreaseRemainingLifetime()
    {
        var result = await Result();
        var remaining = QueryResultRetention.Remaining(result, new ShiftClock { WallMinutes = -60, TimestampMinutes = 4 });
        Assert.InRange(remaining.TotalSeconds, 59, 61);
    }

    [Fact]
    public async Task ExpiredRenderAndHtmlMeasureRejectBeforeReturningDerivedContent()
    {
        var result = await Result(); var clock = new ShiftClock { WallMinutes = 6 };
        var spec = ReportSpecs.Default(result);
        Assert.Throws<QueryResultExpiredException>(() => HtmlReportRenderer.Render(result, spec, clock));
        Assert.Throws<QueryResultExpiredException>(() => DashboardPlans.HtmlMeasure("銷售", "synthetic-html", result, spec, clock));
    }

    [Fact]
    public async Task ExpiredExportDoesNotCreateOutputFolder()
    {
        var result = await Result(); var path = Path.Combine(Path.GetTempPath(), "semantic-retention-" + Guid.NewGuid().ToString("N"));
        var session = new PermissionSession(result.Source, ConversationMode.Report, [Capability.ArtifactWrite]);
        await Assert.ThrowsAsync<QueryResultExpiredException>(() => new HtmlReportExporter(path, session,
            new ShiftClock { WallMinutes = 6 }).ExportAsync(result, session.Snapshot, default));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ExpiredAdapterResultDoesNotReturnRows()
    {
        var model = new DemoModel(); var session = new PermissionSession(DemoModel.Snapshot.Identity,
            ConversationMode.Analyze, [Capability.DataQuery]);
        var call = new ToolCall("synthetic", "execute_dax_query", ContractJson.Element(new { dax = DemoModel.SupportedQuery }));
        var result = await new ToolDispatcher(model, model, session, resultClock: new ShiftClock { WallMinutes = 6 })
            .DispatchAsync(call, session.Snapshot, default);
        Assert.False(result.Success); Assert.Equal("result_expired", result.Code); Assert.Null(result.Query);
    }

    [Fact]
    public async Task ExpiryDuringRenderingRejectsCompletedHtml()
    {
        var result = await Result();
        Assert.Throws<QueryResultExpiredException>(() => HtmlReportRenderer.Render(result, resultClock: new ExpireSecondReadClock()));
    }

    [Fact]
    public async Task DashboardRejectsExpiredResultWithoutUndoingVerifiedMetrics()
    {
        var writer = new DemoDefinitionWriter(); var clock = new ShiftClock();
        var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Report, [Capability.MetadataRead]);
        var core = new DashboardCoordinator(writer, writer, writer, session, resultClock: clock);
        var draft = await core.PrepareAsync(DemoDashboard.Plan, session.Snapshot, default);
        session.Update(draft.Definition.Plan.Target, ConversationMode.Report, [Capability.MetadataRead, Capability.DefinitionWrite]);
        var outcome = await core.ApplyAsync(draft, core.ApproveAfterUserConfirmation(draft, session.Snapshot), session.Snapshot, default);
        Assert.True(outcome.Success);
        session.Update(outcome.After!.Identity, ConversationMode.Report, [Capability.MetadataRead, Capability.DataQuery]);
        clock.WallMinutes = 6;
        await Assert.ThrowsAsync<QueryResultExpiredException>(() => core.QueryAsync(draft, outcome, session.Snapshot, default));
        Assert.Equal(outcome.After.Revision, (await writer.ReadDefinitionAsync(default)).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryBeforeNextRoundOrFinalOutcomeClearsRows(bool expireOnFinal)
    {
        var model = new DemoModel(); var session = new PermissionSession(DemoModel.Snapshot.Identity,
            ConversationMode.Analyze, [Capability.DataQuery]);
        var clock = new ShiftClock(); var provider = new ExpiringProvider(clock, expireOnFinal);
        var events = new List<ConversationEvent>();
        var outcome = await new ConversationCoordinator(model, provider, new(model, model, session), session,
            resultClock: clock).RunAsync(new(Guid.NewGuid(), "合成TTL", ConversationMode.Analyze, session.Snapshot), e => { events.Add(e); return Task.CompletedTask; });
        Assert.Equal("result_expired", outcome.Code); Assert.Empty(outcome.ToolResults);
        Assert.Equal(expireOnFinal ? 2 : 1, provider.Calls);
        Assert.DoesNotContain(events, e => e.Kind == EventKind.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalProviderRejectsExpiryBeforePayloadOrDuringCredentialRead(bool duringCredential)
    {
        var profile = new ProviderIdentity("ttl-fixture", "https://example.invalid/v1/chat/completions", "synthetic");
        var consent = new EgressConsent(profile, [EgressCategory.UserMessage, EgressCategory.QueryResults], DateTimeOffset.UtcNow.AddMinutes(1));
        var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Analyze, [Capability.DataQuery], consent);
        var clock = new ShiftClock(); var handler = new ProbeHandler(); var credential = new ProbeCredential(clock, duringCredential);
        using var provider = new OpenAiCompatibleProvider(profile, credential, handler, clock);
        // Obtain the genuine lease through the coordinator, never forge its internal constructor.
        var capture = new CaptureAuthorizationProvider(provider);
        await new ConversationCoordinator(new DemoModel(), capture, new(new DemoModel(), new DemoModel(), session), session)
            .RunAsync(new(Guid.NewGuid(), "取得合成lease", ConversationMode.Analyze, session.Snapshot), _ => Task.CompletedTask);
        var query = await Result();
        if (!duringCredential) clock.WallMinutes = 6;
        var request = capture.Request! with
        {
            Messages = [new ProviderMessage("tool", "合成結果", Result: new("synthetic", "execute_dax_query", true, "ok", "合成", Query: query))]
        };
        await Assert.ThrowsAsync<QueryResultExpiredException>(async () =>
        { await foreach (var _ in provider.StreamAsync(request, default)) { } });
        Assert.Equal(0, handler.Calls);
        Assert.Equal(duringCredential ? 1 : 0, credential.Reads);
    }

    internal sealed class ShiftClock : TimeProvider
    {
        public int WallMinutes { get; set; }
        public int TimestampMinutes { get; set; }
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddMinutes(WallMinutes);
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + TimestampMinutes * 60L * TimestampFrequency;
    }

    private sealed class ExpireSecondReadClock : TimeProvider
    {
        private int reads;
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddMinutes(++reads >= 2 ? 6 : 0);
    }

    private sealed class ExpiringProvider(ShiftClock clock, bool expireOnFinal) : IAiProvider
    {
        public bool UsesExternalNetwork => false;
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            if (Calls == 1)
            {
                yield return new ProviderEvent.Call(new("query", "execute_dax_query", ContractJson.Element(new { dax = DemoModel.SupportedQuery })));
                if (!expireOnFinal) clock.WallMinutes = 6;
            }
            else { clock.WallMinutes = 6; yield return new ProviderEvent.Text("已送出之前的合成內容無法回收。"); }
        }
    }

    private sealed class CaptureAuthorizationProvider(OpenAiCompatibleProvider actual) : IAiProvider
    {
        public bool UsesExternalNetwork => true;
        public ProviderIdentity? Identity => actual.Identity;
        public ProviderRequest? Request { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new ProviderEvent.Text("只捕捉自有授權，不送HTTP。");
        }
    }

    private sealed class ProbeCredential(ShiftClock clock, bool expire) : ICredentialSource
    {
        public int Reads { get; private set; }
        public string? ReadSecret(ProviderIdentity provider) { Reads++; if (expire) clock.WallMinutes = 6; return null; }
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
