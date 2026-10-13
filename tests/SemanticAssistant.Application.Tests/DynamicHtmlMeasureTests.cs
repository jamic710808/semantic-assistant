using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class DynamicHtmlMeasureTests
{
    private static ModelSnapshot Model(DashboardPlan? plan = null)
    {
        plan ??= DemoDashboard.Plan;
        return DemoModel.Snapshot with
        {
            Tables = DemoModel.Snapshot.Tables.Select(t => t with
            { Measures = t.Measures.AddRange(plan.Metrics.Where(m => m.Table == t.Name).Select(m => new MeasureDefinition(m.Name, m.Expression))) }).ToImmutableArray()
        };
    }
    private static MeasureChange Compile(DashboardPlan? plan = null, ModelSnapshot? model = null, string name = "SA_HTML") =>
        DynamicHtmlMeasureCompiler.Compile(plan ?? DemoDashboard.Plan, model ?? Model(plan), "銷售", name);

    [Fact]
    public void UsesCurrentMeasuresWithoutSnapshotNumbersOrUnfilteredGroups()
    {
        var change = Compile(); Assert.InRange(change.Expression.Length, 1, 8000);
        Assert.Contains("SUMMARIZECOLUMNS('日期'[月份]", change.Expression);
        Assert.Contains("'銷售'[SA_合成銷售]", change.Expression);
        Assert.DoesNotContain("397000", change.Expression); Assert.DoesNotContain("120000", change.Expression);
        Assert.DoesNotContain("ALL('日期'[月份])", change.Expression);
        Assert.Contains("TOPN(100", change.Expression); Assert.Contains("IF(ISBLANK('日期'[月份]), 0, 1), ASC, '日期'[月份], ASC)", change.Expression);
        Assert.Contains("COUNTROWS(__all) > 100", change.Expression); Assert.Contains("LEN(__html) > 64000", change.Expression);
        Assert.Contains("KPI依顯示組別聚合", change.Expression); Assert.Contains("ISNUMBER", change.Expression);
        Assert.Contains("SUMX(__data", change.Expression); Assert.Contains("ISBLANK", change.Expression);
        CheckLexicalBalance(change.Expression);
    }
    [Fact]
    public void ExplicitPeriodIntersectsExistingFiltersWithExclusiveEnd()
    {
        var plan = DemoDashboard.Plan with { Period = new(new("銷售", "日期"), new(2026, 1, 1), new(2026, 4, 1)) };
        var expression = Compile(plan).Expression;
        Assert.Contains("CALCULATETABLE(SUMMARIZECOLUMNS", expression); Assert.Contains("KEEPFILTERS(FILTER(ALL('銷售'[日期])", expression);
        Assert.Contains(">= DATE(2026,1,1)", expression); Assert.Contains("< DATE(2026,4,1)", expression);
        CheckLexicalBalance(expression);
    }
    [Theory]
    [InlineData(VisualKind.Table)]
    [InlineData(VisualKind.Kpi)]
    [InlineData(VisualKind.BarChart)]
    [InlineData(VisualKind.LineChart)]
    public void EachVisualCompilesToFixedBoundedTemplate(VisualKind kind)
    {
        var visual = kind switch
        {
            VisualKind.Table => new VisualSpec("v", "table", kind, null, null, null),
            VisualKind.Kpi => new VisualSpec("v", "kpi", kind, null, 1, Aggregation.First),
            _ => new VisualSpec("v", "chart", kind, 0, 1, null)
        };
        var expression = Compile(DemoDashboard.Plan with { Visuals = [visual] }).Expression;
        Assert.DoesNotContain("<script", expression); Assert.DoesNotContain("http://", expression.Replace("http://www.w3.org/2000/svg", ""));
        if (kind == VisualKind.LineChart)
        {
            Assert.Contains("__prev", expression); Assert.Contains("IF(ISBLANK(__prev)", expression);
            Assert.Contains("<circle", expression); Assert.Contains("stroke-width='2'", expression);
        }
        CheckLexicalBalance(expression);
    }
    [Theory]
    [InlineData(Aggregation.Sum, "SUMX(__data")]
    [InlineData(Aggregation.Count, "FORMAT(__n")]
    [InlineData(Aggregation.First, "MAXX(TOPN(1")]
    public void KpiAggregationIsExplicit(Aggregation aggregation, string expected)
    {
        var plan = DemoDashboard.Plan with { Visuals = [new("k", "KPI", VisualKind.Kpi, null, aggregation == Aggregation.Count ? null : 1, aggregation)] };
        Assert.Contains(expected, Compile(plan).Expression);
    }
    [Fact]
    public void AllFourVisualsFitACompactPlan()
    {
        var plan = DemoDashboard.Plan with { Visuals = DemoDashboard.Plan.Visuals.Add(new("line", "trend", VisualKind.LineChart, 0, 1, null)) };
        var change = Compile(plan); Assert.InRange(change.Expression.Length, 1, 8000); CheckLexicalBalance(change.Expression);
    }
    [Fact]
    public void HtmlAndDaxEscapingAreSeparateAndDynamicLabelsAreEncoded()
    {
        var plan = DemoDashboard.Plan with { Title = "<script>\"'&</script>", Visuals = [new("t", "<img src=x onerror=x>", VisualKind.Table, null, null, null)] };
        var expression = Compile(plan).Expression;
        Assert.Contains("&lt;script&gt;", expression); Assert.Contains("&lt;img", expression);
        Assert.DoesNotContain("<script>", expression); Assert.DoesNotContain("<img", expression);
        foreach (var entity in new[] { "&amp;", "&lt;", "&gt;", "&quot;", "&#39;" }) Assert.Contains(entity, expression);
        Assert.Contains("LEFT(IF(ISBLANK", expression); Assert.Contains(", 256)", expression);
        CheckLexicalBalance(expression);
        var model = Model(plan);
        var renamed = model with
        {
            Tables = model.Tables.Select(t => t.Name == "日期" ? t with
            { Name = "日'期", Columns = t.Columns.Select(c => c.Name == "月份" ? c with { Name = "月]份" } : c).ToImmutableArray() } : t).ToImmutableArray()
        };
        var quoted = Compile(plan with { Grouping = new("日'期", "月]份") }, renamed).Expression;
        Assert.Contains("'日''期'[月]]份]", quoted); CheckLexicalBalance(quoted);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("same-name")]
    [InlineData("reverse-reference")]
    [InlineData("group-type")]
    [InlineData("oversize")]
    public void RejectsMissingChangedRecursiveUnsupportedOrOverSizedPlan(string scenario)
    {
        var plan = DemoDashboard.Plan; var model = Model(); var name = "SA_HTML";
        switch (scenario)
        {
            case "missing": model = DemoModel.Snapshot; break;
            case "changed": model = Model(plan with { Metrics = [plan.Metrics[0] with { Expression = "2" }] }); break;
            case "same-name": name = plan.Metrics[0].Name; break;
            case "reverse-reference": plan = plan with { Metrics = [plan.Metrics[0] with { Expression = "[SA_HTML]" }] }; model = Model(plan); break;
            case "group-type":
                model = model with
                {
                    Tables = model.Tables.Select(t => t.Name == "日期" ? t with
                    { Columns = t.Columns.Select(c => c.Name == "月份" ? c with { DataType = "Boolean" } : c).ToImmutableArray() } : t).ToImmutableArray()
                }; break;
            default: plan = plan with { Visuals = Enumerable.Range(0, 8).Select(i => new VisualSpec("b" + i, "chart", VisualKind.LineChart, 0, 1, null)).ToImmutableArray() }; break;
        }
        Assert.ThrowsAny<Exception>(() => Compile(plan, model, name));
    }
    [Fact]
    public void GeneratedAliasesCannotShadowExistingObjects()
    {
        var plan = DemoDashboard.Plan; var table = "銷售"; var name = "SA_HTML";
        var prefix = "__sa_" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { plan, table, name })))[..8];
        var model = Model(); model = model with
        {
            Tables = model.Tables.Select(t => t.Name == table ? t with
            { Columns = t.Columns.Add(new(prefix + "_m0", "String")) } : t).ToImmutableArray()
        };
        Assert.Throws<InvalidDataException>(() => Compile(plan, model));
    }
    [Fact]
    public async Task DynamicMeasureStillRequiresIndependentWritePreviewAndConsent()
    {
        var writer = new DemoDefinitionWriter();
        var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead));
        var core = new ModelWriteCoordinator(writer, session);
        var metrics = await core.PrepareAsync(DemoDashboard.Plan.Metrics, session.Snapshot, default);
        session.Update(metrics.Plan.Target, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
        var applied = await core.ApplyAsync(metrics, core.ApproveAfterUserConfirmation(metrics, session.Snapshot), session.Snapshot, default);
        Assert.True(applied.Success);
        session.Update(applied.After!.Identity, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead));
        var change = Compile(model: await writer.ReadSnapshotAsync(default));
        var preview = await core.PrepareAsync([change], session.Snapshot, default);
        Assert.Throws<UnauthorizedAccessException>(() => core.ApproveAfterUserConfirmation(preview, session.Snapshot));
        Assert.False((await core.ApplyAsync(preview, null, session.Snapshot, default)).Success);
        session.Update(preview.Plan.Target, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
        Assert.True((await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default)).Success);
        Assert.Equal(change.Expression, (await writer.ReadSnapshotAsync(default)).Tables[0].Measures.Single(m => m.Name == change.Name).Expression);
        // Demo only stores this DAX; its query runner is deliberately not a DAX interpreter.
    }

    // A lexical sanity check, not a DAX parser/engine. Strings and quoted identifiers have separate escaping.
    private static void CheckLexicalBalance(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'' or '[')
            {
                var close = text[i] == '[' ? ']' : text[i]; var done = false;
                while (++i < text.Length)
                {
                    if (text[i] != close) continue;
                    if (i + 1 < text.Length && text[i + 1] == close) { i++; continue; }
                    done = true; break;
                }
                Assert.True(done, "Unclosed DAX literal/identifier.");
            }
            else if (text[i] == '(') depth++;
            else if (text[i] == ')') { depth--; Assert.True(depth >= 0, "Extra closing parenthesis."); }
        }
        Assert.Equal(0, depth);
    }
}
