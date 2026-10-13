using System.Collections.Immutable;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ReportSpecTests
{
    private static Task<QueryResult> Result() => new DemoModel().ExecuteAsync(DemoModel.Snapshot.Identity, DemoModel.SupportedQuery, new(), default);

    [Fact]
    public async Task DefaultReportHasTableKpiAndBarWithActualValues()
    {
        var result = await Result(); var spec = ReportSpecs.Default(result);
        Assert.Equal(new[] { VisualKind.Table, VisualKind.Kpi, VisualKind.BarChart }, spec.Visuals.Select(v => v.Kind));
        var html = HtmlReportRenderer.Render(result, ReportSpecs.Parse(ReportSpecs.Serialize(spec), result));
        Assert.Contains("397,000.00", html); Assert.Contains("<svg", html); Assert.Contains(result.ResultId, html);
        Assert.Contains("default-src 'none'", html); Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public async Task DifferentResultIdIsRejected()
    {
        var result = await Result();
        Assert.Throws<InvalidDataException>(() => HtmlReportRenderer.Render(result, ReportSpecs.Default(result) with { ResultId = "invented" }));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("numeric-enum")]
    [InlineData("unknown-kind")]
    [InlineData("missing-version")]
    public async Task SpecSchemaRejectsUnexpectedOrAmbiguousFields(string kind)
    {
        var result = await Result(); var json = ReportSpecs.Serialize(ReportSpecs.Default(result));
        json = kind switch
        {
            "unknown" => json.Replace("\"Version\":1", "\"Version\":1,\"Html\":\"script\""),
            "duplicate" => json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
            "numeric-enum" => json.Replace("\"Table\"", "0"),
            "unknown-kind" => json.Replace("\"Table\"", "\"CustomScript\""),
            _ => json.Replace("\"Version\":1,", ""),
        };
        Assert.Throws<InvalidDataException>(() => ReportSpecs.Parse(json, result));
    }

    [Fact]
    public async Task InvalidColumnsAndAggregationsAreRejected()
    {
        var result = await Result(); var spec = ReportSpecs.Default(result);
        Assert.Throws<InvalidDataException>(() => HtmlReportRenderer.Render(result, spec with
        { Visuals = [new("x", "x", VisualKind.BarChart, 0, 8, null)] }));
        Assert.Throws<InvalidDataException>(() => HtmlReportRenderer.Render(result, spec with
        { Visuals = [new("x", "x", VisualKind.Kpi, null, 0, Aggregation.Sum)] }));
    }

    [Fact]
    public async Task NegativeAndZeroValuesProduceFiniteSvgAndLine()
    {
        var result = await Result();
        result = result with
        {
            Rows = [ [ContractJson.Element("a"), ContractJson.Element(-5)],
            [ContractJson.Element("b"), ContractJson.Element(0)], [ContractJson.Element("c"), ContractJson.Element(5)] ]
        };
        var spec = ReportSpecs.Default(result) with { Visuals = [new("line", "趨勢", VisualKind.LineChart, 0, 1, null)] };
        var html = HtmlReportRenderer.Render(result, spec);
        Assert.Contains("<polyline", html); Assert.DoesNotContain("NaN", html); Assert.DoesNotContain("Infinity", html);
    }

    [Fact]
    public async Task TitlesAndLabelsAreEncoded()
    {
        var result = await Result();
        result = result with { Rows = [[ContractJson.Element("</text><script>alert(1)</script>"), ContractJson.Element(12)]] };
        var spec = ReportSpecs.Default(result) with { Title = "<script>bad</script>" };
        var html = HtmlReportRenderer.Render(result, spec);
        Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task TruncationAndProvenanceStayVisible()
    {
        var result = (await Result()) with { Truncated = true };
        var html = HtmlReportRenderer.Render(result);
        Assert.Contains("截斷：True", html); Assert.Contains("截斷結果不代表完整母體", html);
        Assert.Contains("合成示範資料", html); Assert.Contains(result.Source.Revision, html);
    }
}
