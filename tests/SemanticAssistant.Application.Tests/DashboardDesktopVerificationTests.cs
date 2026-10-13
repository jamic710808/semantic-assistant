using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class DashboardDesktopVerificationTests
{
    [Fact]
    public void GeneratedPlansAndExpectedValuesAreReviewableWithoutAService()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SemanticAssistant-dashboard-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var root = Path.GetDirectoryName(SyntheticDesktopFixture.CreateProject(parent))!;
        foreach (var variant in new[] { "default", "line", "period" })
        {
            var plan = DashboardPlans.Parse(File.ReadAllText(Path.Combine(root, "plans", "dashboard-" + variant + ".json")));
            var changes = StrictJson.Parse<ImmutableArray<MeasureChange>>(File.ReadAllText(Path.Combine(root, "plans", "dynamic-html-" + variant + ".json")));
            Assert.Equal(DashboardDesktopVerification.Variant(variant).Period, plan.Period);
            Assert.Equal(DashboardDesktopVerification.CompileFixtureVariant(variant), Assert.Single(changes));
        }
        using var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "expected-dashboard.json")));
        Assert.False(expected.RootElement.GetProperty("DaxEngineVerified").GetBoolean());
        Assert.Equal(2, expected.RootElement.GetProperty("North").GetArrayLength());
    }
    [Fact]
    public void DashboardExpectedValuesComeFromIndependentSyntheticRows()
    {
        foreach (var filter in new[] { "All", "North", "South" })
        {
            var expected = filter switch { "North" => DashboardDesktopVerification.NorthRows, "South" => DashboardDesktopVerification.SouthRows, _ => DashboardDesktopVerification.AllRows };
            var input = SyntheticDesktopFixture.Sales.Where(s => filter == "All" || s.RegionKey == (filter == "North" ? 1 : 2));
            var groups = input.GroupBy(s => s.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key).ToArray();
            Assert.Equal(groups.Length, expected.Length);
            for (var i = 0; i < groups.Length; i++)
            {
                Assert.Equal(groups[i].Key, expected[i].Month);
                Assert.Equal(groups[i].Sum(s => s.Revenue), expected[i].Revenue);
                Assert.Equal(groups[i].Sum(s => s.Cost), expected[i].Cost);
                Assert.Equal(groups[i].Sum(s => s.Revenue - s.Cost), expected[i].Profit);
            }
        }
    }
    [Theory]
    [InlineData("default")]
    [InlineData("line")]
    [InlineData("period")]
    public void AllActualFixtureHtmlPlansFitThePublishedDaxBudget(string variant)
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        foreach (var metric in SyntheticDesktopFixture.Dashboard.Metrics)
            database.Model.Tables[metric.Table].Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = metric.Name, Expression = metric.Expression });
        var model = TomModelProjection.Read(database, "fixture");
        var plan = DashboardDesktopVerification.Variant(variant);
        var expression = DynamicHtmlMeasureCompiler.Compile(plan, model, "Sales", "SA_Fixture HTML " + variant).Expression;
        Assert.InRange(expression.Length, 1, DynamicHtmlMeasureCompiler.MaximumExpressionCharacters);
        Assert.DoesNotContain("700.00", expression);
        Assert.DoesNotContain("280.00", expression);
        Assert.Equal(expression, DashboardDesktopVerification.CompileFixtureVariant(variant).Expression);
    }
    [Fact]
    public void HtmlAcceptanceChecksActualTableKpisAndMarksRatherThanOnlyPresenceOfNumbers()
    {
        var rows = DashboardDesktopVerification.AllRows;
        var html = Html(rows);
        DashboardDesktopVerification.ValidateDynamicHtml(html, rows);
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml(html.Replace("700.00", "701.00"), rows));
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml(html.Replace("<td>330.00</td>", "<td>331.00</td>"), rows));
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml(html.Replace("<rect x='1' y='2' width='3' height='4'/>", ""), rows));
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml(html.Replace("x='1'", "x='NaN'"), rows));
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml(html.Replace("<div class='sa'>", "<div class='sa' onclick='x'>"), rows));
    }
    [Fact]
    public void CompiledSvgCoordinatesUseMandatoryDecimalDigits()
    {
        foreach (var variant in new[] { "default", "line", "period" })
        {
            var expression = DashboardDesktopVerification.CompileFixtureVariant(variant).Expression;
            Assert.DoesNotContain("\"0.##\"", expression);
            Assert.Contains("\"0.00\", \"en-US\"", expression);
        }
    }
    [Theory]
    [InlineData("220.")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,234")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1px")]
    [InlineData("1e309")]
    public void SvgCoordinatesRejectInvalidSyntaxOrNonFiniteMagnitude(string value)
    {
        var html = Html(DashboardDesktopVerification.AllRows).Replace("x='1'", "x='" + value + "'");
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml(html, DashboardDesktopVerification.AllRows));
    }
    [Theory]
    [InlineData("220")]
    [InlineData("220.00")]
    [InlineData("-1.5")]
    [InlineData("2.2e2")]
    public void SvgCoordinatesAcceptFiniteSvgNumbers(string value)
    {
        DashboardDesktopVerification.ValidateDynamicHtml(Html(DashboardDesktopVerification.AllRows).Replace("x='1'", "x='" + value + "'"), DashboardDesktopVerification.AllRows);
    }
    [Fact]
    public void EmptyContextRequiresAnExplicitEmptyMessageAndNoVisualSections()
    {
        DashboardDesktopVerification.ValidateDynamicHtml("<div class='sa'><p>目前篩選沒有資料</p></div>", []);
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml("<div class='sa'><p>目前篩選沒有資料</p><section/></div>", []));
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml("<div class='sa'><p>700.00</p></div>", []));
    }
    [Fact]
    public void HtmlAcceptanceCannotResolveExternalEntitiesOrAcceptScripts()
    {
        Assert.Throws<XmlException>(() => DashboardDesktopVerification.ValidateDynamicHtml("<!DOCTYPE div [<!ENTITY x SYSTEM 'file:///private'>]><div class='sa'>&x;</div>", []));
        Assert.Throws<InvalidDataException>(() => DashboardDesktopVerification.ValidateDynamicHtml("<div class='sa'><script>x</script></div>", []));
    }
    private static string Html(ImmutableArray<DashboardExpectedRow> rows)
    {
        static string N(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        var cells = string.Concat(rows.Select(row => $"<tr><td>{row.Month}</td><td>{N(row.Revenue)}</td><td>{N(row.Cost)}</td><td>{N(row.Profit)}</td></tr>"));
        return $"<div class='sa'><section><table><tbody>{cells}</tbody></table></section><section><p class='kpi'>{N(rows.Sum(r => r.Revenue))}</p></section><section><p class='kpi'>{N(rows.Sum(r => r.Profit))}</p></section><section><svg xmlns='http://www.w3.org/2000/svg'>" +
            string.Concat(rows.Select(_ => "<rect x='1' y='2' width='3' height='4'/>")) + "</svg></section></div>";
    }
}
