using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SemanticAssistant.Application;

public enum ReportOutputKind { ReportMeasure, HtmlFile, Dashboard }
public enum ReportLayout { Overview, Sections }
public sealed record TemplateSelection([property: JsonRequired] string TemplateId, [property: JsonRequired] string PaletteId);
public sealed record ReportAppearance(ReportOutputKind Output, string TemplateId, string PaletteId);
public sealed record TemplatePreferences(
    [property: JsonRequired] TemplateSelection ReportMeasure,
    [property: JsonRequired] TemplateSelection HtmlFile,
    [property: JsonRequired] TemplateSelection Dashboard)
{
    public static TemplatePreferences Default => new(new("measure-overview", "navy"), new("file-overview", "navy"), new("dashboard-overview", "teal"));
    public ReportAppearance For(ReportOutputKind output)
    {
        var selection = output switch { ReportOutputKind.ReportMeasure => ReportMeasure, ReportOutputKind.HtmlFile => HtmlFile,
            ReportOutputKind.Dashboard => Dashboard, _ => throw new InvalidDataException("未知報告輸出類型。") };
        if (selection is null) throw new InvalidDataException("缺少範本設定。");
        return new(output, selection.TemplateId, selection.PaletteId);
    }
    public TemplatePreferences With(ReportOutputKind output, TemplateSelection selection) => output switch
    {
        ReportOutputKind.ReportMeasure => this with { ReportMeasure = selection },
        ReportOutputKind.HtmlFile => this with { HtmlFile = selection },
        ReportOutputKind.Dashboard => this with { Dashboard = selection },
        _ => throw new InvalidDataException("未知報告輸出類型。")
    };
    public void Validate() { foreach (var output in Enum.GetValues<ReportOutputKind>()) ReportTemplates.Resolve(For(output)); }
}

public sealed record ReportTemplate(string Id, ReportOutputKind Output, string Name, string Description, ReportLayout Layout);
public sealed record ReportPalette(string Id, string Name, string Accent, string Background, string Card, string Text, string Muted, string Border);
public sealed record ResolvedReportAppearance(ReportAppearance Selection, ReportTemplate Template, ReportPalette Palette);

// Fixed, independently authored templates. Provider input cannot supply CSS, HTML or colors.
public static class ReportTemplates
{
    public static ImmutableArray<ReportTemplate> Templates { get; } =
    [
        new("measure-overview", ReportOutputKind.ReportMeasure, "一頁報告 · 重點總覽", "KPI 置頂、圖表雙欄、明細列於底部，適合快速閱讀。", ReportLayout.Overview),
        new("measure-sections", ReportOutputKind.ReportMeasure, "章節報告 · 詳細閱讀", "按視覺順序逐節呈現，單欄適合閱讀與核對。", ReportLayout.Sections),
        new("file-overview", ReportOutputKind.HtmlFile, "經營快報 · 總覽", "章節導覽、KPI 置頂、圖表雙欄與完整資料明細。", ReportLayout.Overview),
        new("file-sections", ReportOutputKind.HtmlFile, "分析報告 · 分節", "章節導覽與單欄視覺，保留每項分析及查詢來源。", ReportLayout.Sections),
        new("dashboard-overview", ReportOutputKind.Dashboard, "極簡商務看板", "緊湊的 KPI 總覽與雙欄圖表；資料明細置底。", ReportLayout.Overview),
        new("dashboard-sections", ReportOutputKind.Dashboard, "分析明細看板", "單欄排列指標、圖表及明細，適合較窄的報表區域。", ReportLayout.Sections)
    ];
    public static ImmutableArray<ReportPalette> Palettes { get; } =
    [
        new("navy", "經營藏藍", "#234E78", "#F2F5FA", "#FFFFFF", "#18324A", "#596F84", "#D7E0EA"),
        new("teal", "科技深青", "#107D79", "#EDF7F6", "#FFFFFF", "#163C3A", "#577C79", "#CAE2DF"),
        new("amber", "沉穩琥珀", "#AD641E", "#FFF6EC", "#FFFFFF", "#54351B", "#816548", "#EDD9C1"),
        new("green", "清新翠綠", "#078C67", "#EDF9F4", "#FFFFFF", "#174D3C", "#587C6D", "#C9E7DA"),
        new("azure", "明快天藍", "#007FA9", "#EFF8FC", "#FFFFFF", "#194458", "#54798B", "#CCE3ED"),
        new("violet", "創意羅蘭", "#7447C6", "#F6F0FC", "#FFFFFF", "#352250", "#79648F", "#E2D5F0"),
        new("rose", "醒目玫紅", "#B83D65", "#FCF0F5", "#FFFFFF", "#512C3C", "#8A6474", "#ECD4DF"),
        new("slate", "石墨", "#43586C", "#F1F4F6", "#FFFFFF", "#243340", "#667783", "#D6DFE5"),
        new("sapphire", "寶藍", "#285FA5", "#EFF4FC", "#FFFFFF", "#1D3557", "#5A7292", "#D1DEEE"),
        new("burgundy", "勃艮第", "#90344F", "#FAF0F3", "#FFFFFF", "#4B2532", "#80616D", "#E7D2DA")
    ];
    public static ImmutableArray<ReportTemplate> For(ReportOutputKind output) => Templates.Where(t => t.Output == output).ToImmutableArray();
    public static ImmutableArray<ReportPalette> Colors(ReportOutputKind output) => output switch
    {
        ReportOutputKind.ReportMeasure or ReportOutputKind.HtmlFile => Palettes.Take(7).ToImmutableArray(),
        ReportOutputKind.Dashboard => Palettes.Where(p => p.Id is "teal" or "slate" or "sapphire" or "burgundy").ToImmutableArray(),
        _ => throw new InvalidDataException("未知報告輸出類型。")
    };
    public static ResolvedReportAppearance Resolve(ReportAppearance appearance)
    {
        if (appearance is null || !Enum.IsDefined(appearance.Output)) throw new InvalidDataException("範本設定無效。");
        var template = For(appearance.Output).FirstOrDefault(t => t.Id == appearance.TemplateId);
        var palette = Colors(appearance.Output).FirstOrDefault(p => p.Id == appearance.PaletteId);
        if (template is null || palette is null) throw new InvalidDataException("未知或不相容的範本與配色。");
        return new(appearance, template, palette);
    }
    public static IEnumerable<VisualSpec> Ordered(IEnumerable<VisualSpec> visuals, ReportLayout layout) => layout == ReportLayout.Overview
        ? visuals.OrderBy(v => v.Kind == VisualKind.Kpi ? 0 : v.Kind == VisualKind.Table ? 2 : 1) : visuals;
    public static string Class(VisualSpec visual) => visual.Kind switch { VisualKind.Table => "table", VisualKind.Kpi => "metric", _ => "chart" };
    public static string Css(ResolvedReportAppearance style)
    {
        var p = style.Palette;
        var grid = style.Template.Layout == ReportLayout.Overview;
        var padding = style.Selection.Output == ReportOutputKind.Dashboard ? 12 : 18;
        return $".sa{{font:14px system-ui;color:{p.Text};background:{p.Background};padding:{padding}px;border-radius:12px}}" +
            $".sa h1{{color:{p.Accent};margin:0 0 12px;font-size:26px}}.sa h2{{font-size:18px;margin:0 0 12px}}" +
            $".sa .visuals{{display:grid;grid-template-columns:{(grid ? "repeat(2,minmax(0,1fr))" : "minmax(0,1fr)")};gap:14px;margin:20px 0}}" +
            $".sa section{{min-width:0;padding:{padding}px;background:{p.Card};border:1px solid {p.Border};border-radius:10px}}" +
            (grid ? ".sa .table{grid-column:1/-1}" : "") +
            $".sa .kpi{{color:{p.Accent};font-size:32px;margin:8px 0}}.sa small{{color:{p.Muted}}}" +
            $".sa table{{border-collapse:collapse;width:100%}}.sa td,.sa th{{padding:8px;border-bottom:1px solid {p.Border};text-align:left;overflow-wrap:anywhere}}" +
            $".sa svg{{width:100%;height:auto}}.sa text{{font:12px system-ui;fill:{p.Text}}}.sa pre{{white-space:pre-wrap;overflow-wrap:anywhere}}" +
            $".sa nav{{display:flex;flex-wrap:wrap;gap:12px;margin:16px 0}}.sa a{{color:{p.Accent}}}" +
            "@media(max-width:640px){.sa .visuals{grid-template-columns:minmax(0,1fr)}}";
    }
}
