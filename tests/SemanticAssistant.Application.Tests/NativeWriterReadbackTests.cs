using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class NativeWriterReadbackTests
{
    [Fact]
    public void FixedOwnedDefinitionsPassWithoutServerConnection()
    {
        using var database = Written();
        NativeWriterReadbackVerification.ValidateDefinitions(database);
    }

    [Theory]
    [InlineData("measure")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("different-operation")]
    [InlineData("source")]
    public void AlteredOrResealedDifferentOperationCannotReachQueries(string change)
    {
        using var database = Written();
        var sales = database.Model.Tables["Sales"];
        switch (change)
        {
            case "measure": sales.Measures["SA_FixtureCheck Revenue"].Expression = "1"; break;
            case "column": ((CalculatedColumn)sales.Columns["SA_FixtureCheck Profit"]).Expression = "1"; break;
            case "relationship": ((SingleColumnRelationship)database.Model.Relationships["SA_FixtureCheck Review region"]).IsActive = false; break;
            case "different-operation": TomObjectMutations.MarkMeasure(sales.Measures["SA_FixtureCheck Revenue"], Guid.NewGuid()); break;
            case "source": ((MPartitionSource)sales.Partitions[0].Source).Expression = "Sql.Database(\"other\",\"other\")"; break;
        }
        Assert.Throws<InvalidDataException>(() => NativeWriterReadbackVerification.ValidateDefinitions(database));
    }

    private static Database Written()
    {
        var database = SyntheticDesktopFixture.CreateDatabase();
        var changes = SyntheticDesktopFixture.ObjectChanges;
        var operation = Guid.NewGuid();
        var plan = new DefinitionPlan(operation, new("fixture", "fixture", "fixture", false), "fixture", changes.Measures)
        { Columns = changes.Columns, Relationships = changes.Relationships };
        TomObjectMutations.StageApply(database.Model, plan);
        foreach (var change in changes.Measures)
        {
            var measure = new Measure { Name = change.Name, Expression = change.Expression };
            database.Model.Tables[change.Table].Measures.Add(measure);
            TomObjectMutations.MarkMeasure(measure, operation);
        }
        return database;
    }
}
