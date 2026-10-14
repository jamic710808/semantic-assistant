using System.Collections.Immutable;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.Adapters.Demo;

namespace SemanticAssistant.Desktop;

internal static class TemplatePreview
{
    public static QueryResult Result() => new("template-synthetic-preview", DemoModel.Snapshot.Identity, ["月份", "銷售額"],
        [[ContractJson.Element("2026-01"), ContractJson.Element(120000)],
         [ContractJson.Element("2026-02"), ContractJson.Element(145000)],
         [ContractJson.Element("2026-03"), ContractJson.Element(132000)]], false, DemoModel.SupportedQuery);
    public static string Html(ReportAppearance appearance)
    {
        var result = Result();
        return HtmlReportRenderer.Render(result, appearance.Output == ReportOutputKind.Dashboard
            ? DashboardPlans.Report(DemoDashboard.Plan, result) : ReportSpecs.Default(result), appearance: appearance);
    }
    public static void WriteExamples(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        foreach (var template in ReportTemplates.Templates)
        {
            var palette = ReportTemplates.Colors(template.Output)[0];
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, template.Id + ".html"), Html(new(template.Output, template.Id, palette.Id)));
        }
        var plan = DemoDashboard.Plan;
        var model = DemoModel.Snapshot with { Tables = DemoModel.Snapshot.Tables.Select(t => t with
            { Measures = t.Measures.AddRange(plan.Metrics.Where(m => m.Table == t.Name).Select(m => new MeasureDefinition(m.Name, m.Expression))) }).ToImmutableArray() };
        foreach (var template in ReportTemplates.For(ReportOutputKind.Dashboard))
        {
            var change = DynamicHtmlMeasureCompiler.Compile(plan, model, "銷售", "SA_TemplatePreview", new(ReportOutputKind.Dashboard, template.Id, "teal"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, template.Id + ".dax"), change.Expression);
        }
    }
}
