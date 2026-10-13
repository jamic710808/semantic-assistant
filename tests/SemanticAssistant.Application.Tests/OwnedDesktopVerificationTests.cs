using System.Collections.Immutable;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Contracts;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class OwnedDesktopVerificationTests
{
    [Fact]
    public void EngineRowNumberIsAcceptedButNeverProjectedAsAUserColumn()
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        foreach (var table in database.Model.Tables) table.Columns.Add(Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeObject<RowNumberColumn>("{\"type\":\"rowNumber\",\"name\":\"RowNumber-internal\"}"));
        OwnedDesktopVerification.ValidateBaseline(database);
        Assert.Equal(13, TomModelProjection.Read(database, "fixture").Tables.Sum(t => t.Columns.Length));
        database.Model.Tables[0].Columns.Add(Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeObject<RowNumberColumn>("{\"type\":\"rowNumber\",\"name\":\"RowNumber-extra\"}"));
        Assert.Throws<InvalidDataException>(() => OwnedDesktopVerification.ValidateBaseline(database));
    }
    [Fact]
    public void SyntheticBaselineAcceptedWithoutAnyServiceConnection()
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        OwnedDesktopVerification.ValidateBaseline(database);
        foreach (var table in database.Model.Tables)
            Assert.All(table.Measures, m => Assert.DoesNotContain(table.Columns, c => c.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)));
    }
    [Theory]
    [InlineData("marker")]
    [InlineData("source")]
    [InlineData("column")]
    [InlineData("measure")]
    [InlineData("relationship")]
    [InlineData("extra-table")]
    public void ModifiedBaselineCannotReachRefreshOrDax(string change)
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        var sales = database.Model.Tables["Sales"];
        switch (change)
        {
            case "marker": database.Model.Annotations[SyntheticDesktopFixture.MarkerName].Value = "other"; break;
            case "source": ((MPartitionSource)sales.Partitions[0].Source).Expression = "Sql.Database(\"private\", \"private\")"; break;
            case "column": sales.Columns["Revenue amount"].DataType = DataType.String; break;
            case "measure": sales.Measures["Revenue"].Expression = "1"; break;
            case "relationship": ((SingleColumnRelationship)database.Model.Relationships[0]).IsActive = false; break;
            case "extra-table": database.Model.Tables.Add(new Table { Name = "Extra" }); break;
        }
        Assert.Throws<InvalidDataException>(() => OwnedDesktopVerification.ValidateBaseline(database));
    }
    [Fact]
    public void QualifiedAdomdFieldsAndNumericValuesAreComparedExactly()
    {
        var query = SyntheticDesktopFixture.Queries.Single(q => q.Id == "months");
        var rows = query.ExpectedRows.EnumerateArray().Select(row => row.EnumerateObject().Select(p => p.Value.Clone()).ToImmutableArray()).ToImmutableArray();
        var result = new QueryResult("fixture", new("fixture", "fixture", "fixture", true),
            ["Dates[Month]", "Dates[Month number]", "[Revenue]", "[Profit]"], rows, false, query.Dax);
        OwnedDesktopVerification.ValidateResult(query, result);
        Assert.Throws<InvalidDataException>(() => OwnedDesktopVerification.ValidateResult(query, result with { Truncated = true }));
        Assert.Throws<InvalidDataException>(() => OwnedDesktopVerification.ValidateResult(query, result with { Columns = ["Month", "Wrong", "Revenue", "Profit"] }));
        Assert.Throws<InvalidDataException>(() => OwnedDesktopVerification.ValidateResult(query, result with { Rows = rows.SetItem(0, rows[0].SetItem(2, ContractJson.Element(331))) }));
    }
}
