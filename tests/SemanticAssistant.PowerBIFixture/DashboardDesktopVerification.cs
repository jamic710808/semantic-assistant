using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.PowerBIFixture;

public sealed record DashboardExpectedRow(string Month, decimal Revenue, decimal Cost, decimal Profit);

public static class DashboardDesktopVerification
{
    private static readonly Regex SvgNumber = new(@"\A[+-]?(?:[0-9]+|[0-9]*\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    public static ImmutableArray<DashboardExpectedRow> AllRows => [new("2026-01", 330, 200, 130), new("2026-02", 370, 220, 150)];
    public static ImmutableArray<DashboardExpectedRow> NorthRows => [new("2026-01", 180, 110, 70), new("2026-02", 200, 120, 80)];
    public static ImmutableArray<DashboardExpectedRow> SouthRows => [new("2026-01", 150, 90, 60), new("2026-02", 170, 100, 70)];

    public static DashboardPlan Variant(string variant) => variant switch
    {
        "default" => SyntheticDesktopFixture.Dashboard,
        "line" => SyntheticDesktopFixture.Dashboard with
        { Visuals = SyntheticDesktopFixture.Dashboard.Visuals.SetItem(3, SyntheticDesktopFixture.Dashboard.Visuals[3] with { Kind = VisualKind.LineChart }) },
        "period" => SyntheticDesktopFixture.Dashboard with { Period = new(new("Sales", "Date"), new(2026, 1, 1), new(2026, 2, 1)) },
        _ => throw new ArgumentException("Unknown fixed fixture variant.")
    };

    public static MeasureChange CompileFixtureVariant(string variant)
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        foreach (var metric in SyntheticDesktopFixture.Dashboard.Metrics)
            database.Model.Tables[metric.Table].Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = metric.Name, Expression = metric.Expression });
        return DynamicHtmlMeasureCompiler.Compile(Variant(variant), TomModelProjection.Read(database, "fixture-preview"),
            "Sales", "SA_Fixture HTML " + variant);
    }

    public static FixtureQuery ExpectedQuery(ImmutableArray<DashboardExpectedRow> rows, string dax) => new("dashboard", dax,
        ContractJson.Element(rows.Select(row => new Dictionary<string, object>
        {
            ["Month"] = row.Month, ["SA_Fixture Revenue"] = row.Revenue,
            ["SA_Fixture Cost"] = row.Cost, ["SA_Fixture Profit"] = row.Profit
        })));

    // Parses only our bounded HTML fragment. External entities and DTDs are never resolved.
    public static void ValidateDynamicHtml(string html, ImmutableArray<DashboardExpectedRow> expected,
        bool lineChart = false)
    {
        if (string.IsNullOrEmpty(html) || html.Length > DynamicHtmlMeasureCompiler.MaximumOutputCharacters)
            throw new InvalidDataException("Dynamic HTML output length differs.");
        using var reader = XmlReader.Create(new StringReader(html), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000 });
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new InvalidDataException();
        if (root.Name.LocalName != "div" || root.Attribute("class")?.Value != "sa" ||
            document.Descendants().Any(e => e.Name.LocalName == "script" || e.Attributes().Any(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Dynamic HTML structure differs.");
        if (expected.IsEmpty)
        {
            if (!root.Value.Contains("目前篩選沒有資料", StringComparison.Ordinal) || root.Descendants("section").Any())
                throw new InvalidDataException("Dynamic HTML empty result differs.");
            return;
        }
        var table = root.Descendants("table").Single();
        var rows = table.Descendants("tbody").Single().Elements("tr").ToArray();
        if (rows.Length != expected.Length) throw new InvalidDataException("Dynamic HTML group count differs.");
        for (var i = 0; i < rows.Length; i++)
        {
            var cells = rows[i].Elements("td").Select(c => c.Value).ToArray();
            if (cells.Length != 4 || cells[0] != expected[i].Month || Parse(cells[1]) != expected[i].Revenue ||
                Parse(cells[2]) != expected[i].Cost || Parse(cells[3]) != expected[i].Profit)
                throw new InvalidDataException("Dynamic HTML table values differ.");
        }
        var kpis = root.Descendants("p").Where(p => p.Attribute("class")?.Value == "kpi").Select(p => Parse(p.Value)).ToArray();
        if (kpis.Length != 2 || kpis[0] != expected.Sum(r => r.Revenue) || kpis[1] != expected.Sum(r => r.Profit))
            throw new InvalidDataException("Dynamic HTML KPI values differ.");
        XNamespace svg = "http://www.w3.org/2000/svg";
        var chart = root.Descendants(svg + "svg").Single();
        if (lineChart ? chart.Descendants(svg + "circle").Count() != expected.Length : chart.Descendants(svg + "rect").Count() != expected.Length)
            throw new InvalidDataException("Dynamic HTML chart marks differ.");
        if (lineChart && chart.Descendants(svg + "line").Count() != expected.Length)
            throw new InvalidDataException("Dynamic HTML line segments differ.");
        foreach (var number in chart.Descendants().Attributes().Where(a => a.Name.LocalName is "x" or "y" or "width" or "height" or "x1" or "x2" or "y1" or "y2" or "cx" or "cy"))
            if (!SvgNumber.IsMatch(number.Value) ||
                !decimal.TryParse(number.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("Dynamic HTML SVG coordinate differs.");
        static decimal Parse(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    public static async Task RunAsync(DesktopModelAdapter adapter, string ownedFixtureFolder, Action ensure,
        Action<string> setStage, Action<object> addGate, CancellationToken token)
    {
        void Start(string gate) { setStage(gate); ensure(); token.ThrowIfCancellationRequested(); }
        void Pass(string gate, object? details = null)
        { ensure(); token.ThrowIfCancellationRequested(); addGate(new { Gate = gate, Pass = true, Details = details }); Console.WriteLine(gate + "=verified"); }
        var snapshot = await adapter.ReadSnapshotAsync(token);
        var session = new PermissionSession(snapshot.Identity, ConversationMode.Analyze,
            ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
        var writer = adapter.CreateWriter();
        var dashboard = new DashboardCoordinator(adapter, writer, adapter, session);
        Start("dashboard_preview_no_approval");
        var draft = await dashboard.PrepareAsync(SyntheticDesktopFixture.Dashboard, session.Snapshot, token);
        var denied = await dashboard.ApplyAsync(draft, null, session.Snapshot, token);
        if (denied.Code != "approval_denied" || (await writer.ReadDefinitionAsync(token)).Revision != draft.Definition.Before.Revision)
            throw new InvalidDataException("Dashboard unapproved preview changed fixture.");
        Pass("dashboard_preview_no_approval");
        Start("dashboard_write_readback");
        var approval = dashboard.ApproveAfterUserConfirmation(draft, session.Snapshot);
        var written = await dashboard.ApplyAsync(draft, approval, session.Snapshot, token);
        if (!written.Success) throw new InvalidDataException("Dashboard write outcome: " + written.Code);
        Pass("dashboard_write_readback", new { draft.Definition.Plan.OperationId, written.Code });
        snapshot = await adapter.ReadSnapshotAsync(token);
        session.Update(snapshot.Identity, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
        Start("dashboard_query_without_consent");
        try { await dashboard.QueryAsync(draft, written, session.Snapshot, token); throw new InvalidDataException("Dashboard query lacked separate consent."); }
        catch (UnauthorizedAccessException) { }
        Pass("dashboard_query_without_consent");
        Start("dashboard_actual_query");
        session.Update(snapshot.Identity, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite, Capability.DataQuery));
        var result = await dashboard.QueryAsync(draft, written, session.Snapshot, token);
        OwnedDesktopVerification.ValidateResult(ExpectedQuery(AllRows, draft.Query), result);
        Pass("dashboard_actual_query", new { Rows = result.Rows.Length, Revenue = 700, Profit = 280 });
        var exporter = new HtmlReportExporter(Path.Combine(ownedFixtureFolder, "html-acceptance"), session);
        var spec = DashboardPlans.Report(SyntheticDesktopFixture.Dashboard, result);
        Start("dashboard_export_without_consent");
        try { await exporter.ExportAsync(result, session.Snapshot, token, spec); throw new InvalidDataException("Dashboard export lacked separate consent."); }
        catch (UnauthorizedAccessException) { }
        Pass("dashboard_export_without_consent");
        Start("dashboard_html_export");
        session.Update(snapshot.Identity, ConversationMode.Analyze, session.Snapshot.Granted.Add(Capability.ArtifactWrite));
        var export = await exporter.ExportAsync(result, session.Snapshot, token, spec);
        var exportHtml = await File.ReadAllTextAsync(export, token);
        if (!exportHtml.Contains("<p class=\"kpi\">700.00</p>", StringComparison.Ordinal) ||
            !exportHtml.Contains("<p class=\"kpi\">280.00</p>", StringComparison.Ordinal) || !exportHtml.Contains("Content-Security-Policy", StringComparison.Ordinal))
            throw new InvalidDataException("Dashboard exported KPI or CSP differs.");
        Pass("dashboard_html_export", new { File = Path.GetRelativePath(ownedFixtureFolder, export), Sha256 = Hash(exportHtml) });
        foreach (var variant in new[] { "default", "line", "period" })
        {
            var plan = Variant(variant);
            var name = "SA_Fixture HTML " + variant;
            Start("dynamic_html_write_" + variant);
            snapshot = await adapter.ReadSnapshotAsync(token);
            session.Update(snapshot.Identity, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
            var change = DynamicHtmlMeasureCompiler.Compile(plan, snapshot, "Sales", name);
            var writes = new ModelWriteCoordinator(writer, session);
            var preview = await writes.PrepareAsync([change], session.Snapshot, token);
            var consent = writes.ApproveAfterUserConfirmation(preview, session.Snapshot);
            var outcome = await writes.ApplyAsync(preview, consent, session.Snapshot, token);
            if (!outcome.Success) throw new InvalidDataException("Dynamic HTML writer outcome: " + outcome.Code);
            Pass("dynamic_html_write_" + variant, new { ExpressionLength = change.Expression.Length, preview.Plan.OperationId });
            snapshot = await adapter.ReadSnapshotAsync(token);
            session.Update(snapshot.Identity, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DataQuery));
            foreach (var filter in new[] { "All", "North", "South", "Missing" })
            {
                Start("dynamic_html_" + variant + "_" + filter);
                var measure = DashboardPlans.Field(new("Sales", name));
                var value = filter == "All" ? measure : "CALCULATE(" + measure + ", TREATAS({\"" + filter + "\"}, 'Regions'[Region]))";
                var query = "EVALUATE ROW(\"Html\", " + value + ")";
                var queryPermissions = session.Snapshot;
                if (!session.IsCurrent(queryPermissions) || !queryPermissions.Allows(Capability.DataQuery)) throw new UnauthorizedAccessException();
                var htmlResult = await adapter.ExecuteAsync(snapshot.Identity, query, new(1, 64_000, TimeSpan.FromSeconds(30)), token);
                if (!session.IsCurrent(queryPermissions)) throw new UnauthorizedAccessException();
                if (htmlResult.Truncated || htmlResult.Rows.Length != 1 || htmlResult.Rows[0].Length != 1 || htmlResult.Rows[0][0].ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Dynamic HTML query result differs.");
                var html = htmlResult.Rows[0][0].GetString()!;
                var expected = filter switch { "All" => AllRows, "North" => NorthRows, "South" => SouthRows, _ => [] };
                if (variant == "period") expected = expected.Take(1).ToImmutableArray();
                ValidateDynamicHtml(html, expected, variant == "line");
                var output = Path.Combine(ownedFixtureFolder, "html-acceptance", "dynamic-" + variant + "-" + filter + ".html");
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                var document = "<!doctype html><html lang='zh-Hant'><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>Synthetic HTML acceptance</title></head><body>" + html + "</body></html>";
                await file.WriteAsync(Encoding.UTF8.GetBytes(document), token);
                Pass("dynamic_html_" + variant + "_" + filter, new { Rows = expected.Length, Revenue = expected.Sum(r => r.Revenue), Profit = expected.Sum(r => r.Profit), HtmlSha256 = Hash(html), File = Path.GetRelativePath(ownedFixtureFolder, output) });
            }
        }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
