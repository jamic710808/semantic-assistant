using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class DashboardProposalTests
{
    private static PermissionSession Session() => new(DemoModel.Snapshot.Identity, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead));
    private static ToolCall Call(DashboardPlan? plan = null) => new("p", "propose_dashboard", ContractJson.Element(new { plan = DashboardProposals.PlanPayload(plan ?? DemoDashboard.Plan) }));
    [Fact]
    public async Task ProposalReturnsTypedPlanWithoutQueriesOrModelWrites()
    {
        var model = new DemoDefinitionWriter(); var session = Session();
        var before = await model.ReadDefinitionAsync(default);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Call(), session.Snapshot, default);
        Assert.True(result.Success); Assert.Equal("proposal_ready", result.Code); Assert.Null(result.Query);
        Assert.Equal(before.Revision, (await model.ReadDefinitionAsync(default)).Revision);
        var proposal = DashboardProposals.Accept(result, DemoModel.Snapshot);
        Assert.Equal(DemoDashboard.Plan.Title, proposal.Plan.Title); Assert.Equal(DemoModel.Snapshot.Identity, proposal.Source);
        Assert.Equal(DemoDashboard.Plan.Metrics.ToArray(), proposal.Plan.Metrics.ToArray()); DashboardProposals.Validate(proposal, DemoModel.Snapshot);
        Assert.Contains(new ToolDispatcher(model, model, session).AllowedTools(session.Snapshot), t => t.Name == "propose_dashboard");
        Assert.DoesNotContain(new ToolDispatcher(model, model, session).AllowedTools(session.Snapshot), t => t.RequiredCapability == Capability.DefinitionWrite);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"plan\":{},\"write\":true}")]
    [InlineData("{\"plan\":{},\"plan\":{}}")]
    [InlineData("{\"plan\":null}")]
    [InlineData("{\"plan\":{\"Version\":1}}")]
    public async Task InvalidArgumentsAreDeniedBeforeReading(string json)
    {
        var model = new Probe(); var session = Session();
        using var document = JsonDocument.Parse(json);
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(new("p", "propose_dashboard", document.RootElement), session.Snapshot, default);
        Assert.Equal("invalid_arguments", result.Code); Assert.Equal(0, model.Reads); Assert.Equal(0, model.Queries);
    }
    [Theory]
    [InlineData("column")]
    [InlineData("metric")]
    [InlineData("duplicate")]
    [InlineData("period")]
    public async Task InvalidPlanCannotBecomeAProposal(string scenario)
    {
        var plan = scenario switch
        {
            "column" => DemoDashboard.Plan with { Grouping = new("日期", "unknown") },
            "metric" => DemoDashboard.Plan with { Metrics = [new("unknown", "x", "1")] },
            "duplicate" => DemoDashboard.Plan with { Metrics = [DemoDashboard.Plan.Metrics[0], DemoDashboard.Plan.Metrics[0]] },
            _ => DemoDashboard.Plan with { Period = new(new("日期", "月份"), new(2026, 1, 1), new(2026, 2, 1)) }
        };
        var model = new Probe(); var session = Session();
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Call(plan), session.Snapshot, default);
        Assert.Equal("invalid_plan", result.Code); Assert.Equal(0, model.Queries);
    }
    [Fact]
    public async Task SourceChangesRevocationAndAbsentCapabilityRejectProposal()
    {
        var session = Session(); var model = new Probe(); var dispatcher = new ToolDispatcher(model, model, session);
        var prior = session.Snapshot;
        session.Update(prior.Target, prior.Mode, ImmutableHashSet<Capability>.Empty);
        Assert.Equal("stale_permission", (await dispatcher.DispatchAsync(Call(), prior, default)).Code);
        Assert.Equal("permission_denied", (await dispatcher.DispatchAsync(Call(), session.Snapshot, default)).Code); Assert.Equal(0, model.Reads);
        session.Update(prior.Target with { Revision = "changed" }, prior.Mode, prior.Granted);
        Assert.Equal("target_changed", (await dispatcher.DispatchAsync(Call(), session.Snapshot, default)).Code);
    }
    [Fact]
    public async Task AcceptedProposalCannotMoveToAnotherSourceOrAlterItsPlan()
    {
        var model = new DemoModel(); var session = Session();
        var result = await new ToolDispatcher(model, model, session).DispatchAsync(Call(), session.Snapshot, default);
        var proposal = DashboardProposals.Accept(result, DemoModel.Snapshot);
        Assert.Throws<DefinitionConflictException>(() => DashboardProposals.Validate(proposal, DemoModel.Snapshot with { Identity = proposal.Source with { Revision = "changed" } }));
        Assert.Throws<DefinitionConflictException>(() => DashboardProposals.Validate(proposal with { Plan = proposal.Plan with { Title = "altered" } }, DemoModel.Snapshot));
        Assert.Throws<InvalidDataException>(() => DashboardProposals.Accept(result with { Success = false }, DemoModel.Snapshot));
    }
    [Fact]
    public async Task DemoConversationProducesProposalWithMetadataConsentOnly()
    {
        var model = new Probe(); var session = Session();
        var result = await new ConversationCoordinator(model, new DemoAiProvider(), new ToolDispatcher(model, model, session), session)
            .RunAsync(new(Guid.NewGuid(), "規劃Dashboard", ConversationMode.Report, session.Snapshot), _ => Task.CompletedTask);
        Assert.Equal("ok", result.Code); Assert.Contains(result.ToolResults, t => t.ToolName == "propose_dashboard" && t.Success);
        Assert.Equal(0, model.Queries); Assert.False(session.Snapshot.Allows(Capability.DefinitionWrite));
    }
    private sealed class Probe : ISemanticModelReader, IReadOnlyDaxQueryRunner
    {
        public int Reads, Queries;
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(DemoModel.Snapshot); }
        public Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token)
        { Queries++; throw new InvalidOperationException("Proposal must not query."); }
    }
}
