using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Files;
using SemanticAssistant.Application;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class PowerBIFixtureTests
{
    [Fact]
    public void OfficialTomModelHasDocumentedStarSchemaAndWriterUtility()
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        Assert.Equal(1702, database.CompatibilityLevel);
        Assert.Equal(Microsoft.AnalysisServices.CompatibilityMode.PowerBI, database.CompatibilityMode);
        Assert.Equal(4, database.Model.Tables.Count);
        Assert.Equal(13, database.Model.Tables.Sum(t => t.Columns.Count));
        Assert.Equal(5, database.Model.Tables.Sum(t => t.Measures.Count));
        Assert.Equal(SyntheticDesktopFixture.MarkerValue, database.Model.Annotations[SyntheticDesktopFixture.MarkerName].Value);
        Assert.True(database.Model.DiscourageImplicitMeasures);
        Assert.All(database.Model.Relationships.OfType<SingleColumnRelationship>(), r =>
        {
            Assert.True(r.IsActive);
            Assert.Equal(RelationshipEndCardinality.Many, r.FromCardinality);
            Assert.Equal(RelationshipEndCardinality.One, r.ToCardinality);
            Assert.Equal(CrossFilteringBehavior.OneDirection, r.CrossFilteringBehavior);
            Assert.Equal(r.FromColumn.DataType, r.ToColumn.DataType);
        });
        Assert.DoesNotContain(database.Model.Relationships.OfType<SingleColumnRelationship>(), r => r.ToColumn.Table.Name == "Review regions");
        Assert.All(database.Model.Tables["Sales"].Measures, m => Assert.False(string.IsNullOrWhiteSpace(m.FormatString)));
        Assert.All(database.Model.Tables["Sales"].Columns, c => Assert.True(c.IsHidden));
        Assert.True(database.Model.Tables["Dates"].Columns["Month number"].IsAvailableInMDX);
    }

    [Fact]
    public void PartitionsContainOnlyFixedInlineTablesWithoutExternalDataSources()
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        Assert.Empty(database.Model.DataSources);
        Assert.Empty(database.Model.Expressions);
        foreach (var table in database.Model.Tables)
        {
            var partition = Assert.Single(table.Partitions);
            Assert.Equal(ModeType.Import, partition.Mode);
            var source = Assert.IsType<MPartitionSource>(partition.Source).Expression;
            Assert.Contains("#table(", source);
            Assert.Contains("Table.TransformColumnTypes", source);
            foreach (var forbidden in new[] { "Web.", "File.", "Folder.", "Sql.", "Odbc.", "http:", "https:", "C:\\", "D:\\" })
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            foreach (var column in table.Columns.OfType<DataColumn>())
                Assert.Contains("\"" + column.SourceColumn + "\"", source);
        }
    }

    [Fact]
    public void DocumentedTotalsAndGroupResultsAreIndependentlyComputedFromInputRows()
    {
        var rows = SyntheticDesktopFixture.Sales;
        var totals = SyntheticDesktopFixture.Queries.Single(q => q.Id == "totals").ExpectedRows[0];
        Assert.Equal(rows.Sum(r => r.Revenue), totals.GetProperty("Revenue").GetDecimal());
        Assert.Equal(rows.Sum(r => r.Cost), totals.GetProperty("Cost").GetDecimal());
        Assert.Equal(rows.Sum(r => r.Revenue - r.Cost), totals.GetProperty("Profit").GetDecimal());
        Assert.Equal(rows.Sum(r => r.Quantity), totals.GetProperty("Units").GetInt32());
        Assert.Equal(rows.Sum(r => r.Revenue - r.Cost) / rows.Sum(r => r.Revenue), totals.GetProperty("Margin").GetDecimal());
        var regionResults = SyntheticDesktopFixture.Queries.Single(q => q.Id == "regions").ExpectedRows;
        foreach (var group in rows.GroupBy(r => r.RegionKey))
        {
            var expected = regionResults[group.Key - 1];
            Assert.Equal(group.Sum(r => r.Revenue), expected.GetProperty("Revenue").GetDecimal());
            Assert.Equal(group.Sum(r => r.Cost), expected.GetProperty("Cost").GetDecimal());
            Assert.Equal(group.Sum(r => r.Quantity), expected.GetProperty("Units").GetInt32());
        }
        var monthResults = SyntheticDesktopFixture.Queries.Single(q => q.Id == "months").ExpectedRows;
        foreach (var group in rows.GroupBy(r => r.Date.Month))
        {
            var expected = monthResults[group.Key - 1];
            Assert.Equal(group.Sum(r => r.Revenue), expected.GetProperty("Revenue").GetDecimal());
            Assert.Equal(group.Sum(r => r.Revenue - r.Cost), expected.GetProperty("Profit").GetDecimal());
        }
        // Arithmetic proves the fixture expectations, not DAX engine execution.
    }

    [Fact]
    public void CalendarIsContiguousAndEveryFabricatedForeignKeyHasADimensionRow()
    {
        var dates = SyntheticDesktopFixture.Dates;
        Assert.Equal(59, dates.Length);
        Assert.Equal(new DateOnly(2026, 1, 1), dates[0]);
        Assert.Equal(new DateOnly(2026, 2, 28), dates[^1]);
        Assert.Equal(59, dates.Distinct().Count());
        for (var i = 1; i < dates.Length; i++) Assert.Equal(dates[i - 1].AddDays(1), dates[i]);
        Assert.All(SyntheticDesktopFixture.Sales, sale =>
        {
            Assert.Contains(sale.Date, dates);
            Assert.Contains(sale.RegionKey, new[] { 1, 2 });
        });
    }

    [Fact]
    public async Task GeneratedTmdlRoundTripsThroughOfficialReaderWithoutAService()
    {
        var root = Create();
        var definition = Path.Combine(root, SyntheticDesktopFixture.ProjectName + ".SemanticModel", "definition");
        var snapshot = await new TmdlModelReader(definition).ReadSnapshotAsync(default);
        Assert.Equal(SyntheticDesktopFixture.ModelName, snapshot.Identity.Name);
        Assert.False(snapshot.Identity.IsSynthetic); // File adapter never infers synthetic trust from an arbitrary path/name.
        Assert.Equal(4, snapshot.Tables.Length);
        Assert.Equal(13, snapshot.Tables.Sum(t => t.Columns.Length));
        Assert.Equal(5, snapshot.Tables.Sum(t => t.Measures.Length));
        Assert.Equal(2, snapshot.Relationships.Length);
        using var restored = TmdlSerializer.DeserializeDatabaseFromFolder(definition);
        Assert.Equal(SyntheticDesktopFixture.MarkerValue, restored.Model.Annotations[SyntheticDesktopFixture.MarkerName].Value);
        Assert.All(restored.Model.Tables, t => Assert.IsType<MPartitionSource>(Assert.Single(t.Partitions).Source));
    }

    [Fact]
    public void PbipReferencesOnlyItsOwnModelAndContainsASingleBlankAcceptancePage()
    {
        var root = Create();
        using var shortcut = Read(root, SyntheticDesktopFixture.ProjectName + ".pbip");
        Assert.Equal(SyntheticDesktopFixture.ProjectName + ".Report", shortcut.RootElement.GetProperty("artifacts")[0].GetProperty("report").GetProperty("path").GetString());
        using var report = Read(root, SyntheticDesktopFixture.ProjectName + ".Report/definition.pbir");
        var reference = report.RootElement.GetProperty("datasetReference");
        Assert.False(reference.TryGetProperty("byConnection", out _));
        Assert.Equal("../" + SyntheticDesktopFixture.ProjectName + ".SemanticModel", reference.GetProperty("byPath").GetProperty("path").GetString());
        using var page = Read(root, SyntheticDesktopFixture.ProjectName + ".Report/definition/pages/SyntheticAcceptance/page.json");
        Assert.Equal(1280, page.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(720, page.RootElement.GetProperty("height").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(root, "visual.json", SearchOption.AllDirectories));
        using var expected = Read(root, "expected-results.json");
        Assert.False(expected.RootElement.GetProperty("DaxEngineVerified").GetBoolean());
        Assert.Equal(4, expected.RootElement.GetProperty("Cases").GetArrayLength());
    }

    [Fact]
    public void ManifestMatchesEveryGeneratedFileAndKeepsRuntimeAcceptanceUnproven()
    {
        var root = Create();
        using var manifest = Read(root, "fixture-manifest.json");
        Assert.False(manifest.RootElement.GetProperty("DesktopOpened").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("DaxEngineVerified").GetBoolean());
        var records = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
        Assert.Equal(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() - 1, records.Length);
        foreach (var record in records)
        {
            var path = Path.Combine(root, record.GetProperty("Path").GetString()!);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(bytes.Length, record.GetProperty("Bytes").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), record.GetProperty("Sha256").GetString());
        }
    }

    [Fact]
    public void NewProjectPreservesPreviousProjectAndExistingParentFiles()
    {
        var parent = Parent();
        var original = Path.Combine(parent, "original.txt");
        File.WriteAllText(original, "keep");
        var first = SyntheticDesktopFixture.CreateProject(parent);
        var firstBytes = File.ReadAllBytes(first);
        var second = SyntheticDesktopFixture.CreateProject(parent);
        Assert.NotEqual(first, second);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));
        Assert.Equal("keep", File.ReadAllText(original));
    }

    [Fact]
    public void MissingParentFailsWithoutCreatingUnrequestedDirectories()
    {
        var missing = Path.Combine(Path.GetTempPath(), "SemanticAssistant-fixture-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => SyntheticDesktopFixture.CreateProject(missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task GeneratedNativeWriteAndDashboardPlansMatchTheirOwnModelWithoutExecutingWrites()
    {
        var root = Create();
        var snapshot = await new TmdlModelReader(Path.Combine(root, SyntheticDesktopFixture.ProjectName + ".SemanticModel", "definition")).ReadSnapshotAsync(default);
        var dashboard = DashboardPlans.Parse(File.ReadAllText(Path.Combine(root, "plans", "dashboard-plan.json")));
        DashboardPlans.Validate(dashboard, snapshot);
        Assert.Equal("Dates", dashboard.Grouping.Table);
        Assert.Equal("Month", dashboard.Grouping.Column);
        Assert.Equal(3, dashboard.Metrics.Length);
        var changes = StrictJson.Parse<ModelChanges>(File.ReadAllText(Path.Combine(root, "plans", "model-changes.json")));
        Assert.Single(changes.Measures);
        Assert.Single(changes.Columns);
        var relationship = Assert.Single(changes.Relationships);
        Assert.Equal("Review regions", relationship.To.Table);
        Assert.Equal(RelationshipFilter.OneDirection, relationship.CrossFiltering);
        Assert.Equal(ColumnValueType.Decimal, changes.Columns[0].DataType);
        Assert.All(changes.Measures, m => Assert.DoesNotContain(snapshot.Tables.SelectMany(t => t.Measures), existing => existing.Name == m.Name));
    }

    [Fact]
    public void GeneratedMCalendarUsesInvariantCalendarEvenUnderADifferentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            using var database = SyntheticDesktopFixture.CreateDatabase();
            var source = Assert.IsType<MPartitionSource>(database.Model.Tables["Dates"].Partitions[0].Source).Expression;
            Assert.Contains("\"2026-01\"", source);
            Assert.Contains("\"2026-02\"", source);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static string Parent()
    {
        var path = Path.Combine(Path.GetTempPath(), "SemanticAssistant-fixture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static string Create() => Path.GetDirectoryName(SyntheticDesktopFixture.CreateProject(Parent()))!;
    private static JsonDocument Read(string root, string relative) => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relative)));
}
