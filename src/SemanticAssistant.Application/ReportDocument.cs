using System.Collections.Immutable;
using System.Net;
using System.Text;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// The host owns source, request ID and evidence. Markdown is AI-authored content,
// never executable HTML. A later renderer can add rich visuals to this same contract.
public sealed record ReportDocument(Guid Id, ModelIdentity Source, ProviderIdentity? Provider, Guid RequestId,
    ResponseIntent Intent, string UserRequest, string Markdown, QueryResult? Query, ImmutableArray<ToolResult> Evidence)
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public string Title => Markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?.Trim()[2..].Trim()
        ?? (Intent == ResponseIntent.Analyze ? "模型分析" : Intent == ResponseIntent.Dashboard ? "Dashboard 計畫說明" : "分析報告");
    public void EnsureFresh()
    {
        if (Id == Guid.Empty || RequestId == Guid.Empty || Source is null || !Enum.IsDefined(Intent) ||
            string.IsNullOrWhiteSpace(Markdown) || Markdown.Length > 20_000 || Evidence.IsDefault)
            throw new InvalidDataException("報告文件無效。");
        foreach (var evidence in Evidence)
        {
            ToolPolicyEngine.EnsureResultFresh(evidence);
            if (evidence.Query is { } query && query.Source != Source) throw new DefinitionConflictException();
        }
        if (Query is not null)
        {
            if (Query.Source != Source) throw new DefinitionConflictException();
            QueryResultRetention.EnsureFresh(Query);
        }
    }
    public bool IsFresh { get { try { EnsureFresh(); return true; } catch (QueryResultExpiredException) { return false; } } }
}

public static class ReportDocumentRenderer
{
    public static string Render(ReportDocument document, ReportAppearance appearance, ReportSpec? spec = null)
    {
        document.EnsureFresh();
        var style = ReportTemplates.Resolve(appearance);
        static string E(string value) => WebUtility.HtmlEncode(value);
        if (document.Query is { } query)
            return HtmlReportRenderer.Render(query, spec, appearance: appearance, document: document);
        var narrative = Narrative(document);
        var html = "<!doctype html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">" +
            "<title>" + E(document.Title) + "</title><style>body{max-width:1000px;margin:24px auto;padding:16px}" + ReportTemplates.Css(style) +
            "</style></head><body><main class=\"sa\" data-document=\"" + document.Id + "\" data-template=\"" + style.Template.Id +
            "\"><h1>" + E(document.Title) + "</h1><small>" + E(style.Template.Name) + " · " + E(style.Palette.Name) +
            " · " + E(document.Source.Name) + " · " + (document.Source.IsSynthetic ? "合成示範" : "AI 報告，推論仍需核對") +
            "</small>" + narrative + "<small>文件：" + document.Id +
            " · 來源版本：" + E(document.Source.Revision) + "</small></main></body></html>";
        document.EnsureFresh();
        return html;
    }

    public static string Narrative(ReportDocument document)
    {
        document.EnsureFresh();
        static string E(string value) => WebUtility.HtmlEncode(value);
        var body = new StringBuilder();
        var navigation = new StringBuilder();
        var section = false;
        var code = false;
        var index = 0;
        foreach (var line in document.Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            { body.Append(code ? "</pre>" : "<pre>"); code = !code; continue; }
            if (code) { body.Append(E(line)).Append('\n'); continue; }
            if (text.StartsWith("## ", StringComparison.Ordinal))
            {
                if (section) body.Append("</section>");
                var anchor = "section-" + index++;
                var heading = E(text[3..]);
                body.Append($"<section id=\"{anchor}\"><h2>{heading}</h2>"); section = true;
                navigation.Append($"<a href=\"#{anchor}\">{heading}</a>");
            }
            else if (!text.StartsWith("# ", StringComparison.Ordinal) && text.Length > 0)
                body.Append("<p style=\"white-space:pre-wrap\">").Append(E(line)).Append("</p>");
        }
        if (code) body.Append("</pre>");
        if (section) body.Append("</section>");
        document.EnsureFresh();
        return "<nav aria-label=\"報告章節\">" + navigation + "</nav><div class=\"visuals\">" + body + "</div>";
    }
}
