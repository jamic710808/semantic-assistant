using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class PersistenceDesktopVerificationTests
{
    [Fact]
    public void CompleteSyntheticDefinitionAcceptedWithoutConnection()
    {
        using var database = Completed();
        PersistenceDesktopVerification.ValidateCompletedFixture(database);
        Assert.Equal(12, database.Model.Tables["Sales"].Measures.Count);
        Assert.Equal(3, database.Model.Relationships.Count);
    }

    [Fact]
    public void UnwrittenFixtureCannotPassPersistence()
    {
        using var database = SyntheticDesktopFixture.CreateDatabase();
        Assert.Throws<InvalidDataException>(() => PersistenceDesktopVerification.ValidateCompletedFixture(database));
    }

    [Fact]
    public void CompletedDefinitionSurvivesOfficialTmdlRoundTrip()
    {
        using var database = Completed();
        var folder = Path.Combine(Path.GetTempPath(), "sa-persistence-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        TmdlSerializer.SerializeDatabaseToFolder(database, folder);
        using var reopened = TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        PersistenceDesktopVerification.ValidateCompletedFixture(reopened);
        foreach (var measure in database.Model.Tables["Sales"].Measures)
            Assert.Equal(measure.LineageTag, reopened.Model.Tables["Sales"].Measures[measure.Name].LineageTag);
    }

    [Theory]
    [InlineData("measure")]
    [InlineData("missing")]
    [InlineData("owner")]
    [InlineData("operation")]
    [InlineData("integrity")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("source")]
    [InlineData("extra")]
    public void AlteredDefinitionCannotReachDax(string change)
    {
        using var database = Completed();
        var sales = database.Model.Tables["Sales"];
        var measure = sales.Measures["SA_Fixture HTML default"];
        switch (change)
        {
            case "measure": measure.Expression = "1"; break;
            case "missing": sales.Measures.Remove(measure); break;
            case "owner": measure.Annotations[DefinitionRules.OwnerAnnotation].Value = "other"; break;
            case "operation": measure.Annotations[DefinitionRules.OperationAnnotation].Value = "invalid"; break;
            case "integrity": measure.Annotations[DefinitionRules.IntegrityAnnotation].Value = "invalid"; break;
            case "column": ((CalculatedColumn)sales.Columns["SA_FixtureCheck Profit"]).Expression = "1"; break;
            case "relationship": ((SingleColumnRelationship)database.Model.Relationships["SA_FixtureCheck Review region"]).IsActive = false; break;
            case "source": ((MPartitionSource)sales.Partitions[0].Source).Expression = "Sql.Database(\"private\", \"private\")"; break;
            case "extra": sales.Measures.Add(new Measure { Name = "Other", Expression = "1" }); break;
        }
        Assert.Throws<InvalidDataException>(() => PersistenceDesktopVerification.ValidateCompletedFixture(database));
    }

    private static Database Completed()
    {
        var database = SyntheticDesktopFixture.CreateDatabase();
        var operation = Guid.NewGuid();
        var changes = SyntheticDesktopFixture.ObjectChanges;
        var plan = new DefinitionPlan(operation, new ModelIdentity("fixture", "fixture", "fixture", false), "fixture", changes.Measures)
        { Columns = changes.Columns, Relationships = changes.Relationships };
        TomObjectMutations.StageApply(database.Model, plan);
        foreach (var change in changes.Measures.Concat(PersistenceDesktopVerification.DashboardMeasures))
        {
            var measure = new Measure { Name = change.Name, Expression = change.Expression };
            database.Model.Tables[change.Table].Measures.Add(measure);
            TomObjectMutations.MarkMeasure(measure, operation);
        }
        return database;
    }
}
