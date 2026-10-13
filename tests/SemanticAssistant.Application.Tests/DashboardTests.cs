using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class DashboardTests
{
    private static PermissionSession Session() => new(DemoModel.Snapshot.Identity, ConversationMode.Report,
        ImmutableHashSet.Create(Capability.MetadataRead));
    private static string Json(DashboardPlan plan) => JsonSerializer.Serialize(plan, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
    private static void Grant(PermissionSession session, ModelIdentity target, params Capability[] capabilities) =>
        session.Update(target, ConversationMode.Report, capabilities.Append(Capability.MetadataRead).ToImmutableHashSet());

    [Fact]
    public async Task PreviewDoesNotWriteAndAllThreeCapabilitiesRemainIndependent()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new DashboardCoordinator(writer, writer, writer, session);
        var draft = await core.PrepareAsync(DemoDashboard.Plan, session.Snapshot, default);
        Assert.Equal("demo-definition-0", (await writer.ReadDefinitionAsync(default)).Revision);
        Assert.Throws<UnauthorizedAccessException>(() => core.ApproveAfterUserConfirmation(draft, session.Snapshot));
        Grant(session, draft.Definition.Plan.Target, Capability.DefinitionWrite);
        Assert.False((await core.ApplyAsync(draft, null, session.Snapshot, default)).Success);
        var outcome = await core.ApplyAsync(draft, core.ApproveAfterUserConfirmation(draft, session.Snapshot), session.Snapshot, default);
        Assert.True(outcome.Success);
        Grant(session, outcome.After!.Identity);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => core.QueryAsync(draft, outcome, session.Snapshot, default));
        Grant(session, outcome.After.Identity, Capability.DataQuery);
        var result = await core.QueryAsync(draft, outcome, session.Snapshot, default);
        Assert.Equal(3, result.Rows.Length); Assert.Equal(397000, result.Rows.Sum(r => r[1].GetInt32()));
        var folder = Path.Combine(Path.GetTempPath(), "sa-dashboard-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new HtmlReportExporter(folder, session).ExportAsync(result, session.Snapshot, default));
        Assert.False(Directory.Exists(folder));
    }
    [Theory]
    [InlineData("unknown-column")]
    [InlineData("unknown-table")]
    [InlineData("bad-period-column")]
    [InlineData("reversed-period")]
    [InlineData("bad-visual")]
    public async Task InvalidFieldsPeriodAndVisualsAreDeniedBeforeWriting(string scenario)
    {
        var plan = scenario switch
        {
            "unknown-column" => DemoDashboard.Plan with { Grouping = new("日期", "不存在") },
            "unknown-table" => DemoDashboard.Plan with { Grouping = new("不存在", "月份") },
            "bad-period-column" => DemoDashboard.Plan with { Period = new(new("日期", "月份"), new(2026, 1, 1), new(2026, 4, 1)) },
            "reversed-period" => DemoDashboard.Plan with { Period = new(new("銷售", "日期"), new(2026, 4, 1), new(2026, 1, 1)) },
            _ => DemoDashboard.Plan with { Visuals = [new("bad", "bad", VisualKind.BarChart, 0, 9, null)] }
        };
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new DashboardCoordinator(writer, writer, writer, session);
        await Assert.ThrowsAsync<InvalidDataException>(() => core.PrepareAsync(plan, session.Snapshot, default));
        Assert.Equal("demo-definition-0", (await writer.ReadDefinitionAsync(default)).Revision);
    }
    [Fact]
    public async Task OwnedObjectCollisionIsDeniedBeforeWriting()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new DashboardCoordinator(writer, writer, writer, session);
        await Assert.ThrowsAsync<DefinitionConflictException>(() => core.PrepareAsync(DemoDashboard.Plan with
        { Metrics = [new("銷售", "銷售額", "1")] }, session.Snapshot, default));
    }
    [Fact]
    public async Task ChangedVisualsOrQueryCannotReuseAnIssuedDraft()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new DashboardCoordinator(writer, writer, writer, session);
        var draft = await core.PrepareAsync(DemoDashboard.Plan, session.Snapshot, default);
        Grant(session, draft.Definition.Plan.Target, Capability.DefinitionWrite);
        Assert.Throws<UnauthorizedAccessException>(() => core.ApproveAfterUserConfirmation(draft with { Query = "EVALUATE ROW(\"x\",1)" }, session.Snapshot));
        Assert.Throws<UnauthorizedAccessException>(() => core.ApproveAfterUserConfirmation(draft with { Plan = draft.Plan with { Title = "other" } }, session.Snapshot));
    }
    [Fact]
    public void StrictPlanJsonRejectsMissingDuplicateUnknownAndNumericEnums()
    {
        var json = Json(DemoDashboard.Plan); Assert.Equal(1, DashboardPlans.Parse(json).Version);
        Assert.ThrowsAny<Exception>(() => DashboardPlans.Parse(json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1")));
        Assert.ThrowsAny<Exception>(() => DashboardPlans.Parse(json.Replace("\"Version\":1,", "")));
        Assert.ThrowsAny<Exception>(() => DashboardPlans.Parse(json.Replace("\"Version\":1", "\"Version\":1,\"ArbitraryScript\":\"x\"")));
        Assert.ThrowsAny<Exception>(() => DashboardPlans.Parse(json.Replace("\"BarChart\"", "2")));
        Assert.ThrowsAny<Exception>(() => StrictJson.Parse<ImmutableArray<MeasureChange>>("[{\"Table\":\"銷售\",\"Name\":\"SA_x\",\"Expression\":\"1\",\"Expression\":\"2\"}]"));
    }
    [Fact]
    public void ExplicitPeriodGeneratesStartInclusiveEndExclusiveAndOrderedQuery()
    {
        var plan = DemoDashboard.Plan with { Period = new(new("銷售", "日期"), new(2026, 1, 1), new(2026, 4, 1)) };
        DashboardPlans.Validate(plan, DemoModel.Snapshot); var query = DashboardPlans.BuildQuery(plan);
        Assert.Contains("FILTER(ALL('銷售'[日期])", query); Assert.Contains(">= DATE(2026,1,1)", query);
        Assert.Contains("< DATE(2026,4,1)", query); Assert.EndsWith("ORDER BY '日期'[月份]", query);
    }
    [Fact]
    public async Task ForgedOutcomeAndChangedOwnedMeasureCannotGrantAQuery()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new DashboardCoordinator(writer, writer, writer, session);
        var draft = await core.PrepareAsync(DemoDashboard.Plan, session.Snapshot, default);
        Grant(session, draft.Definition.Plan.Target, Capability.DataQuery);
        var before = await writer.ReadDefinitionAsync(default);
        await Assert.ThrowsAsync<DefinitionConflictException>(() => core.QueryAsync(draft, new("fake", true, false, "", before), session.Snapshot, default));
    }
    [Fact]
    public async Task HtmlMeasureUsesValidatedEscapedSnapshotAndRejectsOverSizedOutput()
    {
        var result = await new DemoModel().ExecuteAsync(DemoModel.Snapshot.Identity, DemoModel.SupportedQuery, new(), default);
        var spec = ReportSpecs.Default(result) with { Title = "<script>alert(1)</script>" };
        var change = DashboardPlans.HtmlMeasure("銷售", "SA_HTML", result, spec);
        Assert.StartsWith("\"<!doctype html>", change.Expression); Assert.EndsWith("\"", change.Expression);
        Assert.Contains("&lt;script&gt;", change.Expression); Assert.DoesNotContain("<script>", change.Expression);
        Assert.Contains("lang=\"\"zh-Hant\"\"", change.Expression);
        Assert.Throws<InvalidDataException>(() => DashboardPlans.HtmlMeasure("銷售", "SA_HTML", result, spec with { ResultId = "different" }));
        var longValue = result with { Rows = Enumerable.Range(0, 80).Select(_ => ImmutableArray.Create(ContractJson.Element(new string('x', 200)), ContractJson.Element(1))).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => DashboardPlans.HtmlMeasure("銷售", "SA_HTML", longValue, ReportSpecs.Default(longValue)));
    }
    [Fact]
    public void EmptyQueryResultsDoNotInventMetrics()
    {
        var result = new QueryResult("empty", DemoModel.Snapshot.Identity, [], [], false, "query");
        var spec = DashboardPlans.Report(DemoDashboard.Plan, result);
        Assert.Single(spec.Visuals); Assert.Equal(VisualKind.Table, spec.Visuals[0].Kind);
        Assert.Contains("沒有推測欄位或數值", HtmlReportRenderer.Render(result, spec));
    }
}
