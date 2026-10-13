using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// Render values from an accepted query result. No model-generated HTML or script.
public static class HtmlReportRenderer
{
    public static string Render(QueryResult result, ReportSpec? spec = null, TimeProvider? resultClock = null)
    {
        QueryResultRetention.EnsureFresh(result, resultClock);
        spec ??= ReportSpecs.Default(result);
        ReportSpecs.Validate(spec, result);
        static string Encode(string value) => WebUtility.HtmlEncode(value);
        var body = new StringBuilder();
        foreach (var row in result.Rows)
        {
            body.Append("<tr>");
            foreach (var value in row)
            {
                var display = value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
                    ? number.ToString("N0", CultureInfo.GetCultureInfo("zh-TW"))
                    : value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                body.Append("<td>").Append(Encode(display)).Append("</td>");
            }
            body.Append("</tr>");
        }
        var visualHtml = new StringBuilder();
        foreach (var visual in spec.Visuals)
        {
            visualHtml.Append("<section><h2>").Append(Encode(visual.Title)).Append("</h2>");
            if (visual.Kind == VisualKind.Table)
                visualHtml.Append("<table><thead><tr>").Append(string.Concat(result.Columns.Select(c => $"<th>{Encode(c)}</th>")))
                    .Append("</tr></thead><tbody>").Append(body).Append("</tbody></table>");
            else if (visual.Kind == VisualKind.Kpi)
            {
                var value = visual.Aggregation == Aggregation.Count ? result.Rows.Length : visual.Aggregation == Aggregation.First
                    ? result.Rows[0][visual.ValueColumn!.Value].GetDecimal() : result.Rows.Sum(r => r[visual.ValueColumn!.Value].GetDecimal());
                visualHtml.Append("<p class=\"kpi\">").Append(Encode(value.ToString("N2", CultureInfo.GetCultureInfo("zh-TW"))))
                    .Append("</p><small>聚合：").Append(visual.Aggregation).Append("；依目前查詢結果計算。截斷結果不代表完整母體。</small>");
            }
            else visualHtml.Append(RenderChart(result, visual));
            visualHtml.Append("</section>");
        }
        var html = "<!doctype html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">" +
            "<title>Semantic Assistant 報告</title><style>body{font:16px system-ui;max-width:900px;margin:48px auto;padding:24px;color:#17324d}table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #ddd;text-align:left;padding:12px}small{color:#526477}pre{white-space:pre-wrap}section{margin:28px 0;padding:20px;background:#f2f5fa;border-radius:12px}.kpi{font-size:36px;margin:8px 0}svg{width:100%;height:auto}text{font:12px system-ui;fill:#17324d}</style></head><body>" +
            $"<h1>{Encode(spec.Title)}</h1><p>{(result.Source.IsSynthetic ? "合成示範資料，非真實營運數值。" : "資料來自已授權查詢。")}</p>" +
            $"<small>結果識別碼：{Encode(result.ResultId)} · 版本：{Encode(result.Source.Revision)} · 截斷：{result.Truncated}</small>" +
            (result.Rows.IsEmpty ? "<p>查詢未回傳資料列；沒有推測欄位或數值。</p>" : "") + visualHtml + "<h2>查詢來源</h2><pre>" + Encode(result.Query) + "</pre></body></html>";
        QueryResultRetention.EnsureFresh(result, resultClock);
        return html;
    }

    private static string RenderChart(QueryResult result, VisualSpec visual)
    {
        static string N(decimal n) => n.ToString("0.##", CultureInfo.InvariantCulture);
        var values = result.Rows.Select(r => r[visual.ValueColumn!.Value].GetDecimal()).ToArray();
        var min = Math.Min(0, values.Min()); var max = Math.Max(0, values.Max());
        var range = max - min; if (range == 0) range = 1;
        decimal Y(decimal n) => 220 - (n - min) / range * 180;
        var step = 640m / values.Length; var baseline = Y(0);
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 720 280\" role=\"img\"><title>");
        svg.Append(WebUtility.HtmlEncode(visual.Title)).Append("</title><line x1=\"50\" x2=\"690\" y1=\"")
            .Append(N(baseline)).Append("\" y2=\"").Append(N(baseline)).Append("\" stroke=\"#8799ad\"/>");
        var points = new List<string>();
        for (var i = 0; i < values.Length; i++)
        {
            var x = 50 + step * (i + .5m); var y = Y(values[i]);
            points.Add(N(x) + "," + N(y));
            var tooltip = WebUtility.HtmlEncode(result.Rows[i][visual.LabelColumn!.Value].ToString() + "：" + values[i].ToString(CultureInfo.InvariantCulture));
            if (visual.Kind == VisualKind.BarChart)
                svg.Append($"<rect x=\"{N(x - step * .35m)}\" y=\"{N(Math.Min(y, baseline))}\" width=\"{N(step * .7m)}\" height=\"{N(Math.Abs(y - baseline))}\" fill=\"#296ec5\"><title>{tooltip}</title></rect>");
            else svg.Append($"<circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"4\" fill=\"#296ec5\"><title>{tooltip}</title></circle>");
            if (values.Length <= 12)
                svg.Append($"<text x=\"{N(x)}\" y=\"250\" text-anchor=\"middle\">{WebUtility.HtmlEncode(result.Rows[i][visual.LabelColumn!.Value].ToString())}</text>");
        }
        if (visual.Kind == VisualKind.LineChart)
            svg.Append($"<polyline points=\"{string.Join(" ", points)}\" fill=\"none\" stroke=\"#296ec5\" stroke-width=\"2\"/>");
        return svg.Append("</svg><small>依查詢回傳順序繪製；完整數值可從表格核對。</small>").ToString();
    }
}
