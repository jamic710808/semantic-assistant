using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// Generates our fixed HTML/SVG templates as DAX. No rows, AI HTML, or scripts are input.
// Template/serialization tests do not establish acceptance by an actual DAX engine.
public static class DynamicHtmlMeasureCompiler
{
    public const int MaximumGroups = 100;
    public const int MaximumExpressionCharacters = 8000;
    public const int MaximumOutputCharacters = 64000;

    public static MeasureChange Compile(DashboardPlan plan, ModelSnapshot model, string table, string name, ReportAppearance? appearance = null)
    {
        DashboardPlans.Validate(plan, model);
        var style = ReportTemplates.Resolve(appearance ?? TemplatePreferences.Default.For(ReportOutputKind.Dashboard));
        if (style.Selection.Output != ReportOutputKind.Dashboard) throw new InvalidDataException("動態看板需使用 Dashboard 範本。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || model.Tables.All(t => t.Name != table))
            throw new InvalidDataException("HTML 度量值的表或名稱無效。");
        var group = model.Tables.Single(t => t.Name == plan.Grouping.Table).Columns.Single(c => c.Name == plan.Grouping.Column);
        if (group.DataType is not ("String" or "Int64" or "Double" or "Decimal" or "DateTime" or "Date"))
            throw new InvalidDataException("動態 HTML 分組需為文字、日期或數值欄位。");
        foreach (var metric in plan.Metrics)
        {
            if (string.Equals(metric.Name, name, StringComparison.OrdinalIgnoreCase) ||
                metric.Expression.Contains("[" + name.Replace("]", "]]") + "]", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("HTML 度量值不可與指標互相引用。");
            var existing = model.Tables.SingleOrDefault(t => t.Name == metric.Table)?.Measures.SingleOrDefault(m => m.Name == metric.Name);
            if (existing?.Expression != metric.Expression) throw new DefinitionConflictException();
        }
        // Every generated column has a deterministic private alias; reject even a hash-prefix collision.
        var prefix = "__sa_" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { plan, table, name, appearance = style.Selection })))[..8];
        var label = prefix + "_label"; var index = prefix + "_i";
        var aliases = plan.Metrics.Select((_, i) => prefix + "_m" + i).ToArray();
        var names = model.Tables.SelectMany(t => t.Columns.Select(c => c.Name).Concat(t.Measures.Select(m => m.Name)))
            .Concat(plan.Metrics.Select(m => m.Name)).Append(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (aliases.Append(label).Append(index).Any(names.Contains)) throw new InvalidDataException("產生的欄位別名與模型物件碰撞。");
        var field = DashboardPlans.Field(plan.Grouping);
        var summarize = "SUMMARIZECOLUMNS(" + field + ", " + string.Join(", ", plan.Metrics.Select((m, i) =>
            Literal(aliases[i]) + ", " + DashboardPlans.Field(new(m.Table, m.Name)))) + ")";
        if (plan.Period is { } period)
        {
            var dateField = DashboardPlans.Field(period.Field);
            summarize = "CALCULATETABLE(" + summarize + ", KEEPFILTERS(FILTER(ALL(" + dateField + "), " +
                dateField + " >= " + Date(period.Start) + " && " + dateField + " < " + Date(period.End) + ")))";
        }
        var expression = new StringBuilder("VAR __all = ").Append(summarize)
            .Append("\nVAR __rows = TOPN(100, __all, IF(ISBLANK(").Append(field).Append("), 0, 1), ASC, ").Append(field).Append(", ASC)")
            .Append("\nVAR __n = COUNTROWS(__rows)")
            .Append("\nVAR __bad = COUNTROWS(FILTER(__rows, ")
            .Append(string.Join(" || ", aliases.Select(a => $"IF(ISNUMBER({Ref(a)}), ABS({Ref(a)}) > 1000000000000000000000000, NOT(ISBLANK({Ref(a)})))")))
            .Append("))\nVAR __data = ADDCOLUMNS(__rows, ").Append(Literal(index)).Append(", ")
            // Explicit blank handling gives a unique order even when DAX's loose comparisons equate blank and zero.
            .Append("VAR __g = ").Append(field).Append(" RETURN 1 + COUNTROWS(FILTER(__rows, IF(ISBLANK(__g), FALSE(), IF(ISBLANK(")
            .Append(field).Append("), TRUE(), ").Append(field).Append(" < __g)))), ")
            .Append(Literal(label)).Append(", ").Append(Escape("LEFT(IF(ISBLANK(" + field + "), " + Literal("（空白）") + ", " + field + " & \"\"), 256)"))
            .Append(')');
        for (var i = 0; i < plan.Visuals.Length; i++)
            expression.Append("\nVAR __v").Append(i).Append(" = ").Append(RenderVisual(plan, plan.Visuals[i], aliases, label, index, style.Palette));

        var header = "<div class='sa' lang='zh-Hant' data-template='" + style.Template.Id + "' data-palette='" + style.Palette.Id + "'><style>" + ReportTemplates.Css(style) + "</style><h1>" + WebUtility.HtmlEncode(plan.Title) + "</h1>" +
            "<small>依目前篩選內容計算；空白分組先列，其後依分組欄遞增排序，最多100組。標籤最多256字元。KPI依顯示組別聚合，非完整母體合計。全指標空白的組別省略；折線為分類順序，非連續時間尺度。" +
            (model.Identity.IsSynthetic ? "合成示範模型。" : "") + "</small>";
        expression.Append("\nVAR __html = ").Append(Literal(header))
            .Append(" & IF(COUNTROWS(__all) > 100, ").Append(Literal("<p>超過100組，已截斷。</p>")).Append(", \"\")")
            .Append(" & IF(__n = 0, ").Append(Literal("<p>目前篩選沒有資料；沒有推測數值。</p>"))
            .Append(", ").Append(Literal("<div class='visuals'>")).Append(" & ")
            .Append(string.Join(" & ", ReportTemplates.Ordered(plan.Visuals, style.Template.Layout).Select(v => "__v" + plan.Visuals.IndexOf(v))))
            .Append(" & ").Append(Literal("</div>")).Append(')')
            .Append(" & ").Append(Literal("</div>"))
            .Append("\nRETURN IF(__bad > 0, ").Append(Literal("<p>指標有非數字或超出允許範圍的值；無法繪製。</p>"))
            .Append(", IF(LEN(__html) > 64000, ").Append(Literal("<p>HTML內容超過64000字元；請減少視覺或分組。</p>"))
            .Append(", __html))");
        if (expression.Length > MaximumExpressionCharacters)
            throw new InvalidDataException("動態 HTML DAX 超過8000字上限；請減少視覺、指標或標題長度。");
        return new(table, name, expression.ToString());
    }

    private static string RenderVisual(DashboardPlan plan, VisualSpec visual, string[] aliases, string label, string index, ReportPalette palette)
    {
        var open = Literal("<section class='" + ReportTemplates.Class(visual) + "'><h2>" + WebUtility.HtmlEncode(visual.Title) + "</h2>");
        string body;
        if (visual.Kind == VisualKind.Table)
        {
            var head = "<table><thead><tr><th>" + WebUtility.HtmlEncode(plan.Grouping.Column) + "</th>" +
                string.Concat(plan.Metrics.Select(m => "<th>" + WebUtility.HtmlEncode(m.Name) + "</th>")) + "</tr></thead><tbody>";
            var cells = string.Join(" & ", aliases.Select(a => Literal("</td><td>") + " & " + Display(Ref(a))));
            body = Literal(head) + " & CONCATENATEX(__data, " + Literal("<tr><td>") + " & " + Ref(label) + " & " + cells +
                " & " + Literal("</td></tr>") + ", \"\", " + Ref(index) + ", ASC) & " + Literal("</tbody></table>");
        }
        else if (visual.Kind == VisualKind.Kpi)
        {
            var value = visual.Aggregation switch
            {
                Aggregation.Count => "__n",
                Aggregation.First => "MAXX(TOPN(1, __data, " + Ref(index) + ", ASC), " + Ref(aliases[visual.ValueColumn!.Value - 1]) + ")",
                _ => "SUMX(__data, " + Ref(aliases[visual.ValueColumn!.Value - 1]) + ")"
            };
            body = Literal("<p class='kpi'>") + " & " + Display(value) + " & " + Literal("</p><small>聚合：" + visual.Aggregation + "；空白保持空白，Sum略過空白。</small>");
        }
        else body = Chart(visual, Ref(aliases[visual.ValueColumn!.Value - 1]), Ref(label), Ref(index), palette);
        return open + " & (" + body + ") & " + Literal("</section>");
    }

    private static string Chart(VisualSpec visual, string value, string label, string index, ReportPalette palette)
    {
        var opening = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 720 280' role='img'><title>" + WebUtility.HtmlEncode(visual.Title) + "</title>";
        var b = new StringBuilder("VAR __valid = FILTER(__data, NOT(ISBLANK(").Append(value).Append(")))")
            .Append(" VAR __lo = MIN(0, MINX(__valid, ").Append(value).Append("))")
            .Append(" VAR __hi = MAX(0, MAXX(__valid, ").Append(value).Append("))")
            .Append(" VAR __range = IF(__hi = __lo, 1, __hi - __lo)")
            .Append(" VAR __base = 220 - DIVIDE(0 - __lo, __range) * 180")
            .Append(" VAR __step = DIVIDE(640, __n, 0)")
            .Append(" RETURN ").Append(Literal(opening)).Append(" & ")
            .Append(Join(Literal("<line x1='50' x2='690' y1='"), Number("__base"), Literal("' y2='"), Number("__base"), Literal("' stroke='" + palette.Border + "'/>")))
            .Append(" & CONCATENATEX(__valid, VAR __i = ").Append(index)
            .Append(" VAR __x = 50 + __step * (__i - 0.5)")
            .Append(" VAR __y = 220 - DIVIDE(").Append(value).Append(" - __lo, __range) * 180 ");
        string mark;
        var tooltip = Join(Literal("<title>"), label, Literal("："), Display(value), Literal("</title>"));
        if (visual.Kind == VisualKind.BarChart)
            mark = Join(Literal("<rect x='"), Number("__x - __step * 0.35"), Literal("' y='"), Number("MIN(__y, __base)"),
                Literal("' width='"), Number("__step * 0.7"), Literal("' height='"), Number("ABS(__y - __base)"), Literal("' fill='" + palette.Accent + "'>"), tooltip, Literal("</rect>"));
        else
        {
            // Connect only adjacent nonblank groups; missing values remain gaps.
            b.Append("VAR __prev = MAXX(FILTER(__data, ").Append(index).Append(" = __i - 1), ").Append(value).Append(") ")
                .Append("VAR __py = 220 - DIVIDE(__prev - __lo, __range) * 180 ");
            var segment = Join(Literal("<line x1='"), Number("__x - __step"), Literal("' y1='"), Number("__py"), Literal("' x2='"), Number("__x"),
                Literal("' y2='"), Number("__y"), Literal("' stroke='" + palette.Accent + "' stroke-width='2'/>"));
            mark = "IF(ISBLANK(__prev), \"\", " + segment + ") & " + Join(Literal("<circle cx='"), Number("__x"), Literal("' cy='"),
                Number("__y"), Literal("' r='4' fill='" + palette.Accent + "'>"), tooltip, Literal("</circle>"));
        }
        var caption = Join(Literal("<text x='"), Number("__x"), Literal("' y='250' text-anchor='middle'>"), label, Literal("</text>"));
        return b.Append("RETURN ").Append(mark).Append(" & IF(__n <= 12, ").Append(caption).Append(", \"\"), \"\", ")
            .Append(index).Append(", ASC) & ").Append(Literal("</svg><small>空白不繪製；排序依分組欄，完整數值請核對表格。</small>")).ToString();
    }

    private static string Literal(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static string Ref(string name) => "[" + name + "]";
    private static string Join(params string[] expressions) => string.Join(" & ", expressions);
    // DAX FORMAT's optional decimals can leave a trailing dot (e.g. "220.").
    // SVG coordinates require digits after a decimal point; emit both explicitly.
    private static string Number(string expression) => "FORMAT(" + expression + ", \"0.00\", \"en-US\")";
    private static string Display(string expression) => "IF(ISBLANK(" + expression + "), \"—\", FORMAT(" + expression + ", \"#,##0.00\", \"en-US\"))";
    private static string Date(DateOnly value) => string.Create(CultureInfo.InvariantCulture, $"DATE({value.Year},{value.Month},{value.Day})");
    private static string Escape(string expression)
    {
        foreach (var pair in new[] { ("&", "&amp;"), ("<", "&lt;"), (">", "&gt;"), ("\"", "&quot;"), ("'", "&#39;") })
            expression = "SUBSTITUTE(" + expression + ", " + Literal(pair.Item1) + ", " + Literal(pair.Item2) + ")";
        return expression;
    }
}
