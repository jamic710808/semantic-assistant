using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Application;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

public sealed record SyntheticSale(DateOnly Date, int RegionKey, decimal Revenue, decimal Cost, int Quantity);
public sealed record FixtureQuery(string Id, string Dax, JsonElement ExpectedRows);

/// <summary>Self-authored offline test input. No Desktop/service connection, key, source file or external data connector.</summary>
public static class SyntheticDesktopFixture
{
    public const string ProjectName = "SemanticAssistantSynthetic";
    public const string ModelName = "Semantic Assistant Synthetic Desktop";
    public const string MarkerName = "SemanticAssistant.Fixture";
    public const string MarkerValue = "desktop-v1";
    public static ImmutableArray<SyntheticSale> Sales { get; } = [
        new(new(2026, 1, 1), 1, 100m, 60m, 2), new(new(2026, 1, 1), 2, 150m, 90m, 3),
        new(new(2026, 1, 2), 1, 80m, 50m, 1), new(new(2026, 2, 1), 1, 200m, 120m, 4),
        new(new(2026, 2, 1), 2, 120m, 70m, 2), new(new(2026, 2, 2), 2, 50m, 30m, 1)];
    public static ImmutableArray<DateOnly> Dates { get; } = Enumerable.Range(0, 59).Select(i => new DateOnly(2026, 1, 1).AddDays(i)).ToImmutableArray();
    public static DashboardPlan Dashboard { get; } = new(1, "Synthetic revenue and profit", new("Dates", "Month"), null,
        [new("Sales", "SA_Fixture Revenue", "SUM('Sales'[Revenue amount])"), new("Sales", "SA_Fixture Cost", "SUM('Sales'[Cost amount])"),
         new("Sales", "SA_Fixture Profit", "[SA_Fixture Revenue] - [SA_Fixture Cost]")],
        [new("table", "Months and metrics", VisualKind.Table, null, null, null),
         new("revenue", "Revenue in returned months", VisualKind.Kpi, null, 1, Aggregation.Sum),
         new("profit", "Profit in returned months", VisualKind.Kpi, null, 3, Aggregation.Sum),
         new("trend", "Revenue by month", VisualKind.BarChart, 0, 1, null)]);
    public static ModelChanges ObjectChanges { get; } = new(
        [new("Sales", "SA_FixtureCheck Revenue", "SUM('Sales'[Revenue amount])")],
        [new("Sales", "SA_FixtureCheck Profit", "'Sales'[Revenue amount] - 'Sales'[Cost amount]", ColumnValueType.Decimal)],
        [new("SA_FixtureCheck Review region", new("Sales", "Region key"), new("Review regions", "Region key"), true,
            RelationshipCardinality.Many, RelationshipCardinality.One, RelationshipFilter.OneDirection)]);

    public static Database CreateDatabase()
    {
        var database = new Database
        {
            Name = ModelName,
            ID = "semantic-assistant-synthetic-desktop-v1",
            CompatibilityLevel = 1702,
            CompatibilityMode = Microsoft.AnalysisServices.CompatibilityMode.PowerBI,
            Model = new Model
            {
                Name = "Model",
                Culture = "en-US",
                SourceQueryCulture = "en-US",
                DefaultPowerBIDataSourceVersion = PowerBIDataSourceVersion.PowerBI_V3,
                DiscourageImplicitMeasures = true
            }
        };
        database.Model.Annotations.Add(new Annotation { Name = MarkerName, Value = MarkerValue });
        database.Model.Annotations.Add(new Annotation { Name = "PBI_QueryOrder", Value = "[\"Sales\",\"Dates\",\"Regions\",\"Review regions\"]" });
        var sales = Table("Sales", "Six fabricated sales rows; revenue, cost and quantity are fixture values.",
            MTable(["Date", "Region key", "Revenue amount", "Cost amount", "Quantity"], Sales.Select(s =>
                $"{{{MDate(s.Date)}, {s.RegionKey}, {Number(s.Revenue)}, {Number(s.Cost)}, {s.Quantity}}}"),
                "{\"Date\", type date}, {\"Region key\", Int64.Type}, {\"Revenue amount\", Currency.Type}, {\"Cost amount\", Currency.Type}, {\"Quantity\", Int64.Type}"));
        sales.Columns.Add(Column("Date", DataType.DateTime, true));
        sales.Columns.Add(Column("Region key", DataType.Int64, true));
        sales.Columns.Add(Column("Revenue amount", DataType.Decimal, true));
        sales.Columns.Add(Column("Cost amount", DataType.Decimal, true));
        sales.Columns.Add(Column("Quantity", DataType.Int64, true));
        var dates = Table("Dates", "Contiguous calendar from 2026-01-01 through 2026-02-28; 59 dates.",
            MTable(["Date", "Year", "Month", "Month number"], Dates.Select(d =>
                $"{{{MDate(d)}, {d.Year}, \"{d.ToString("yyyy-MM", CultureInfo.InvariantCulture)}\", {d.Month}}}"),
                "{\"Date\", type date}, {\"Year\", Int64.Type}, {\"Month\", type text}, {\"Month number\", Int64.Type}"));
        dates.DataCategory = "Time";
        dates.Columns.Add(Column("Date", DataType.DateTime));
        dates.Columns.Add(Column("Year", DataType.Int64));
        dates.Columns.Add(Column("Month", DataType.String));
        dates.Columns.Add(Column("Month number", DataType.Int64, true));
        dates.Columns["Month"].SortByColumn = dates.Columns["Month number"];
        dates.Columns["Month number"].IsAvailableInMDX = true;
        var regions = RegionTable("Regions", "Two fabricated regions; active filter dimension.");
        var review = RegionTable("Review regions", "Disconnected utility for explicit relationship-writer acceptance; no baseline filter path.");
        foreach (var table in new[] { sales, dates, regions, review }) database.Model.Tables.Add(table);
        database.Model.Relationships.Add(Link("Sales dates", sales.Columns["Date"], dates.Columns["Date"]));
        database.Model.Relationships.Add(Link("Sales regions", sales.Columns["Region key"], regions.Columns["Region key"]));
        foreach (var measure in new[] {
            Measure("Revenue", "SUM('Sales'[Revenue amount])", "#,##0.00", "Sum of fabricated revenue."),
            Measure("Cost", "SUM('Sales'[Cost amount])", "#,##0.00", "Sum of fabricated costs."),
            Measure("Profit", "[Revenue] - [Cost]", "#,##0.00", "Revenue less cost in current filter context."),
            Measure("Margin", "DIVIDE([Profit], [Revenue])", "0.00%", "Profit divided by revenue; blank at zero revenue."),
            Measure("Units", "SUM('Sales'[Quantity])", "#,##0", "Sum of fabricated quantities.") }) sales.Measures.Add(measure);
        return database;
    }

    public static ImmutableArray<FixtureQuery> Queries { get; } = [
        new("totals", "EVALUATE ROW(\"Revenue\", [Revenue], \"Cost\", [Cost], \"Profit\", [Profit], \"Margin\", [Margin], \"Units\", [Units])",
            JsonSerializer.SerializeToElement(new[] { new { Revenue = 700m, Cost = 420m, Profit = 280m, Margin = 0.4m, Units = 13 } })),
        new("regions", "EVALUATE SUMMARIZECOLUMNS('Regions'[Region], \"Revenue\", [Revenue], \"Cost\", [Cost], \"Profit\", [Profit], \"Units\", [Units]) ORDER BY 'Regions'[Region]",
            JsonSerializer.SerializeToElement(new[] { new { Region = "North", Revenue = 380m, Cost = 230m, Profit = 150m, Units = 7 }, new { Region = "South", Revenue = 320m, Cost = 190m, Profit = 130m, Units = 6 } })),
        new("months", "EVALUATE SUMMARIZECOLUMNS('Dates'[Month], 'Dates'[Month number], \"Revenue\", [Revenue], \"Profit\", [Profit]) ORDER BY 'Dates'[Month number]",
            JsonSerializer.SerializeToElement(new[] { new { Month = "2026-01", MonthNumber = 1, Revenue = 330m, Profit = 130m }, new { Month = "2026-02", MonthNumber = 2, Revenue = 370m, Profit = 150m } })),
        new("counts", "EVALUATE ROW(\"SalesRows\", COUNTROWS('Sales'), \"DateRows\", COUNTROWS('Dates'), \"RegionRows\", COUNTROWS('Regions'), \"ReviewRegionRows\", COUNTROWS('Review regions'))",
            JsonSerializer.SerializeToElement(new[] { new { SalesRows = 6, DateRows = 59, RegionRows = 2, ReviewRegionRows = 2 } }))];

    public static string CreateProject(string existingParent)
    {
        var parent = Path.GetFullPath(existingParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException();
        for (var node = new DirectoryInfo(parent); node is not null; node = node.Parent)
            if (node.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Fixture parent cannot traverse a link.");
        var root = Path.Combine(parent, ProjectName + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Fixture already exists.");
        Directory.CreateDirectory(root);
        var definition = Path.Combine(root, ProjectName + ".SemanticModel", "definition");
        Directory.CreateDirectory(definition);
        using (var database = CreateDatabase()) TmdlSerializer.SerializeDatabaseToFolder(database, definition);
        Write(root, ProjectName + ".pbip", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json",
            ["version"] = "1.0",
            ["artifacts"] = new[] { new { report = new { path = ProjectName + ".Report" } } },
            ["settings"] = new { enableAutoRecovery = false }
        }));
        Write(root, ProjectName + ".SemanticModel/definition.pbism", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
            ["version"] = "4.2",
            ["settings"] = new { qnaEnabled = false }
        }));
        Write(root, ProjectName + ".Report/definition.pbir", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definitionProperties/2.0.0/schema.json",
            ["version"] = "4.0",
            ["datasetReference"] = new { byPath = new { path = "../" + ProjectName + ".SemanticModel" } }
        }));
        var report = ProjectName + ".Report/definition/";
        Write(root, report + "version.json", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/versionMetadata/1.0.0/schema.json",
            ["version"] = "2.0.0"
        }));
        Write(root, report + "report.json", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/report/3.1.0/schema.json",
            ["themeCollection"] = new { }
        }));
        Write(root, report + "pages/pages.json", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/pagesMetadata/1.0.0/schema.json",
            ["pageOrder"] = new[] { "SyntheticAcceptance" },
            ["activePageName"] = "SyntheticAcceptance"
        }));
        Write(root, report + "pages/SyntheticAcceptance/page.json", Json(new Dictionary<string, object?>
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/page/2.0.0/schema.json",
            ["name"] = "SyntheticAcceptance",
            ["displayName"] = "Synthetic acceptance",
            ["displayOption"] = "FitToPage",
            ["width"] = 1280,
            ["height"] = 720
        }));
        Write(root, ".gitignore", "**/.pbi/\n");
        foreach (var query in Queries) Write(root, "queries/" + query.Id + ".dax", query.Dax);
        Write(root, "plans/dashboard-plan.json", Json(Dashboard));
        Write(root, "plans/model-changes.json", Json(ObjectChanges));
        Write(root, "plans/measure-changes.json", Json(ObjectChanges.Measures));
        foreach (var variant in new[] { "default", "line", "period" })
        {
            Write(root, "plans/dashboard-" + variant + ".json", Json(DashboardDesktopVerification.Variant(variant)));
            Write(root, "plans/dynamic-html-" + variant + ".json", Json(new[] { DashboardDesktopVerification.CompileFixtureVariant(variant) }));
        }
        Write(root, "expected-dashboard.json", Json(new { FixtureVersion = 1, Synthetic = true, DaxEngineVerified = false,
            All = DashboardDesktopVerification.AllRows, North = DashboardDesktopVerification.NorthRows, South = DashboardDesktopVerification.SouthRows,
            Missing = Array.Empty<DashboardExpectedRow>(), PeriodStart = "2026-01-01", PeriodEndExclusive = "2026-02-01" }));
        Write(root, "expected-results.json", Json(new { FixtureVersion = 1, Synthetic = true, DaxEngineVerified = false, Cases = Queries }));
        Write(root, "README.md", "# Independent synthetic Desktop fixture\n\nOpen SemanticAssistantSynthetic.pbip in a new Power BI Desktop instance and refresh. All partitions are inline fabricated Power Query tables, without files, URLs or credentials. Compare queries/*.dax with expected-results.json. Dates has 59 rows, Sales has 6, Regions and Review regions have 2 each. Review regions is an intentionally disconnected writer-test utility. No Desktop or DAX engine acceptance has been inferred from generating these files.\n");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file => new
        {
            Path = Path.GetRelativePath(root, file).Replace('\\', '/'),
            Bytes = new FileInfo(file).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()
        }).ToArray();
        Write(root, "fixture-manifest.json", Json(new
        {
            FormatVersion = 1,
            Fixture = MarkerValue,
            Synthetic = true,
            ModelName,
            SourceRows = 69,
            Tables = 4,
            Measures = 5,
            Relationships = 2,
            DesktopOpened = false,
            DaxEngineVerified = false,
            Files = files
        }));
        return Path.Combine(root, ProjectName + ".pbip");
    }

    private static Table RegionTable(string name, string description)
    {
        var table = Table(name, description, MTable(["Region key", "Region"], ["{1, \"North\"}", "{2, \"South\"}"], "{\"Region key\", Int64.Type}, {\"Region\", type text}"));
        table.Columns.Add(Column("Region key", DataType.Int64, true));
        table.Columns.Add(Column("Region", DataType.String));
        return table;
    }
    private static Table Table(string name, string description, string source)
    {
        var table = new Table { Name = name, Description = description };
        table.Partitions.Add(new Partition { Name = name, Mode = ModeType.Import, Source = new MPartitionSource { Expression = source } });
        return table;
    }
    private static DataColumn Column(string name, DataType type, bool hidden = false) => new()
    {
        Name = name,
        SourceColumn = name,
        DataType = type,
        IsHidden = hidden,
        IsAvailableInMDX = !hidden,
        SummarizeBy = AggregateFunction.None,
        Description = "Self-authored synthetic fixture field.",
        FormatString = type == DataType.DateTime ? "yyyy-MM-dd" : ""
    };
    private static Measure Measure(string name, string expression, string format, string description) => new()
    {
        Name = name,
        Expression = expression,
        FormatString = format,
        Description = description,
        DisplayFolder = "Synthetic acceptance"
    };
    private static SingleColumnRelationship Link(string name, Column from, Column to) => new()
    {
        Name = name,
        FromColumn = from,
        ToColumn = to,
        FromCardinality = RelationshipEndCardinality.Many,
        ToCardinality = RelationshipEndCardinality.One,
        CrossFilteringBehavior = CrossFilteringBehavior.OneDirection,
        IsActive = true
    };
    private static string MTable(string[] columns, IEnumerable<string> rows, string types) =>
        "let\n    Source = #table({" + string.Join(", ", columns.Select(c => "\"" + c + "\"")) + "}, {\n        " + string.Join(",\n        ", rows) + "\n    }),\n    Typed = Table.TransformColumnTypes(Source, {" + types + "}, \"en-US\")\nin\n    Typed";
    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string MDate(DateOnly date) => $"#date({date.Year}, {date.Month}, {date.Day})";
    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    });
    private static void Write(string root, string relative, string value)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value.Replace("\r\n", "\n")); writer.Write('\n');
    }
}
