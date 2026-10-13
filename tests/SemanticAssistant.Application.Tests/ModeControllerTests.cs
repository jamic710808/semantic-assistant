using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ModeControllerTests
{
    private static PermissionSession Session(ConversationMode mode = ConversationMode.Analyze, params Capability[] grants) =>
        new(DemoModel.Snapshot.Identity, mode, grants.ToImmutableHashSet());
    private static ToolCall Query => new("synthetic", "execute_dax_query", ContractJson.Element(new { dax = DemoModel.SupportedQuery }));

    [Theory]
    [InlineData(AssistantMode.Analyze, ModeRoute.Conversation, Capability.DataQuery)]
    [InlineData(AssistantMode.Report, ModeRoute.Conversation, Capability.DataQuery)]
    [InlineData(AssistantMode.HtmlFile, ModeRoute.HtmlFile, Capability.ArtifactWrite)]
    [InlineData(AssistantMode.HtmlMeasure, ModeRoute.HtmlMeasure, Capability.DefinitionWrite)]
    [InlineData(AssistantMode.Dashboard, ModeRoute.Dashboard, Capability.DefinitionWrite)]
    public void ExactlyFiveTypedRoutesRequireFreshCapabilities(AssistantMode mode, ModeRoute route, Capability capability)
    {
        Assert.Equal(5, ModeController.Definitions.Length);
        var core = new ModeController(mode, DemoModel.Snapshot.Identity); var state = core.Snapshot;
        core.EnsureRoute(state, route);
        var session = Session(ModeController.Get(mode).PermissionMode);
        Assert.Throws<ModePolicyException>(() => core.Check(state, session.Snapshot, session, capability));
        Assert.Empty(session.Snapshot.Granted);
        session.Update(state.Target, session.Snapshot.Mode, [capability]);
        core.Check(state, session.Snapshot, session, capability);
    }

    [Fact]
    public void InvalidModeCannotChangeCurrentState()
    {
        var modes = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity); var state = modes.Snapshot;
        Assert.Throws<ModePolicyException>(() => modes.Change((AssistantMode)999, state.Target));
        Assert.Equal(state, modes.Snapshot);
        Assert.Throws<ModePolicyException>(() => new ModeController((AssistantMode)999, state.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedModeOrRevisionInvalidatesOldState(bool targetChange)
    {
        var modes = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity); var old = modes.Snapshot;
        modes.Change(targetChange ? AssistantMode.Analyze : AssistantMode.Report,
            targetChange ? old.Target with { Revision = "new" } : old.Target);
        Assert.Throws<ModePolicyException>(() => modes.EnsureRoute(old, ModeRoute.Conversation));
        Assert.False(modes.Allows(old, Capability.DataQuery));
    }

    [Fact]
    public void ForeignControllerAndAlteredPermissionTargetAreRejected()
    {
        var modes = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity);
        var foreign = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity);
        Assert.Throws<ModePolicyException>(() => modes.EnsureRoute(foreign.Snapshot, ModeRoute.Conversation));
        var session = Session(ConversationMode.Analyze, Capability.DataQuery);
        Assert.Throws<ModePolicyException>(() => modes.Check(modes.Snapshot,
            session.Snapshot with { Target = session.Snapshot.Target with { Revision = "forged" } }, session, Capability.DataQuery));
    }

    [Theory]
    [InlineData(AssistantMode.Analyze, Capability.DefinitionWrite)]
    [InlineData(AssistantMode.Report, Capability.ArtifactWrite)]
    [InlineData(AssistantMode.HtmlFile, Capability.DataQuery)]
    [InlineData(AssistantMode.HtmlMeasure, Capability.ExternalTool)]
    public void ForgedCapabilityCannotExpandModeMatrix(AssistantMode mode, Capability capability)
    {
        var modes = new ModeController(mode, DemoModel.Snapshot.Identity);
        var session = Session(ModeController.Get(mode).PermissionMode, capability);
        Assert.Throws<ModePolicyException>(() => modes.Check(modes.Snapshot, session.Snapshot, session, capability));
    }

    [Theory]
    [InlineData(AssistantMode.HtmlFile)]
    [InlineData(AssistantMode.HtmlMeasure)]
    [InlineData(AssistantMode.Dashboard)]
    public async Task NativeModeCannotDispatchAgentQueryEvenWithForgedGrant(AssistantMode mode)
    {
        var reader = new Probe(); var modes = new ModeController(mode, DemoModel.Snapshot.Identity);
        var session = Session(ModeController.Get(mode).PermissionMode, Capability.DataQuery);
        var dispatcher = new ToolDispatcher(reader, reader, session, modes: modes, modeSnapshot: modes.Snapshot);
        Assert.Empty(dispatcher.AllowedTools(session.Snapshot));
        Assert.Equal("invalid_mode", (await dispatcher.DispatchAsync(Query, session.Snapshot, default)).Code);
        Assert.Equal(0, reader.Reads); Assert.Equal(0, reader.Queries);
        var provider = new SwitchProvider(modes, false);
        var outcome = await new ConversationCoordinator(reader, provider, dispatcher, session, modes: modes, modeSnapshot: modes.Snapshot)
            .RunAsync(new(Guid.NewGuid(), "合成", session.Snapshot.Mode, session.Snapshot), _ => Task.CompletedTask);
        Assert.Equal("invalid_mode", outcome.Code); Assert.Equal(0, provider.Calls); Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task ModeChangeDuringReadStopsBeforeQuery()
    {
        var modes = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity); var state = modes.Snapshot;
        var reader = new Probe { AfterRead = () => modes.Change(AssistantMode.Report, state.Target) };
        var session = Session(ConversationMode.Analyze, Capability.DataQuery);
        var result = await new ToolDispatcher(reader, reader, session, modes: modes, modeSnapshot: state)
            .DispatchAsync(Query, session.Snapshot, default);
        Assert.Equal("invalid_mode", result.Code); Assert.Null(result.Query); Assert.Equal(0, reader.Queries);
    }

    [Fact]
    public async Task ModeChangeDuringQueryDiscardsLateRows()
    {
        var modes = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity); var state = modes.Snapshot;
        var reader = new Probe { AfterQuery = () => modes.Change(AssistantMode.Report, state.Target) };
        var session = Session(ConversationMode.Analyze, Capability.DataQuery);
        var result = await new ToolDispatcher(reader, reader, session, modes: modes, modeSnapshot: state).DispatchAsync(Query, session.Snapshot, default);
        Assert.Equal("invalid_mode", result.Code); Assert.Null(result.Query);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeChangeBeforeNextRoundOrLateEventStopsProvider(bool beforeEvent)
    {
        var modes = new ModeController(AssistantMode.Analyze, DemoModel.Snapshot.Identity); var state = modes.Snapshot;
        var reader = new Probe(); var session = Session(ConversationMode.Analyze, Capability.DataQuery);
        var provider = new SwitchProvider(modes, beforeEvent); var events = new List<ConversationEvent>();
        var result = await new ConversationCoordinator(reader, provider,
            new(reader, reader, session, modes: modes, modeSnapshot: state), session, modes: modes, modeSnapshot: state)
            .RunAsync(new(Guid.NewGuid(), "合成", ConversationMode.Analyze, session.Snapshot), e => { events.Add(e); return Task.CompletedTask; });
        Assert.Equal("invalid_mode", result.Code); Assert.Empty(result.ToolResults); Assert.Equal(1, provider.Calls);
        if (beforeEvent) Assert.DoesNotContain(events, e => e.Kind == EventKind.Text);
    }

    [Fact]
    public async Task StaleHtmlFileModeCreatesNoOutput()
    {
        var modes = new ModeController(AssistantMode.HtmlFile, DemoModel.Snapshot.Identity); var state = modes.Snapshot;
        var session = Session(ConversationMode.Report, Capability.ArtifactWrite);
        var result = await new DemoModel().ExecuteAsync(state.Target, DemoModel.SupportedQuery, new(), default);
        var folder = Path.Combine(Path.GetTempPath(), "semantic-mode-export-" + Guid.NewGuid().ToString("N"));
        modes.Change(AssistantMode.Report, state.Target);
        await Assert.ThrowsAsync<ModePolicyException>(() => new HtmlReportExporter(folder, session,
            modeGuard: () => modes.Check(state, session.Snapshot, session, Capability.ArtifactWrite)).ExportAsync(result, session.Snapshot, default));
        Assert.False(Directory.Exists(folder));
    }

    private sealed class Probe : ISemanticModelReader, IReadOnlyDaxQueryRunner
    {
        public int Reads { get; private set; }
        public int Queries { get; private set; }
        public Action? AfterRead { get; init; }
        public Action? AfterQuery { get; init; }
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        { Reads++; AfterRead?.Invoke(); return Task.FromResult(DemoModel.Snapshot); }
        public async Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken cancellationToken)
        { Queries++; var result = await new DemoModel().ExecuteAsync(target, dax, limits, cancellationToken); AfterQuery?.Invoke(); return result; }
    }

    private sealed class SwitchProvider(ModeController modes, bool beforeEvent) : IAiProvider
    {
        public bool UsesExternalNetwork => false;
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            if (beforeEvent)
            { modes.Change(AssistantMode.Report, modes.Snapshot.Target); yield return new ProviderEvent.Text("late"); }
            else { yield return new ProviderEvent.Call(Query); modes.Change(AssistantMode.Report, modes.Snapshot.Target); }
        }
    }
}
