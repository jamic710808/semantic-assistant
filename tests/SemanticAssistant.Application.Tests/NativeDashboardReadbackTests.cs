using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class NativeDashboardReadbackTests
{
    [Fact]
    public void FixedDashboardAndSeparateHtmlOperationPassWithoutConnection()
    {
        using var database = Completed();
        NativeDashboardReadbackVerification.ValidateDefinitions(database);
    }

    [Theory]
    [InlineData("metric")]
    [InlineData("metric-operation")]
    [InlineData("html")]
    [InlineData("shared-html-operation")]
    [InlineData("html-owner")]
    [InlineData("html-integrity")]
    [InlineData("source")]
    [InlineData("extra")]
    [InlineData("missing")]
    public void AlteredOrNonIndependentDefinitionCannotReachQueries(string change)
    {
        using var database = Completed();
        var sales = database.Model.Tables["Sales"];
        var html = sales.Measures["SA_Fixture HTML default"];
        switch (change)
        {
            case "metric": sales.Measures["SA_Fixture Revenue"].Expression = "1"; break;
            case "metric-operation": TomObjectMutations.MarkMeasure(sales.Measures["SA_Fixture Revenue"], Guid.NewGuid()); break;
            case "html": html.Expression = "\"other\""; break;
            case "shared-html-operation": TomObjectMutations.MarkMeasure(html, TomObjectMutations.ReadMeasure(sales.Measures["SA_Fixture Revenue"]).OperationId!.Value); break;
            case "html-owner": html.Annotations[DefinitionRules.OwnerAnnotation].Value = "other"; break;
            case "html-integrity": html.Annotations[DefinitionRules.IntegrityAnnotation].Value = "invalid"; break;
            case "source": ((MPartitionSource)sales.Partitions[0].Source).Expression = "Sql.Database(\"other\",\"other\")"; break;
            case "extra": sales.Measures.Add(new Measure { Name = "Other", Expression = "1" }); break;
            case "missing": sales.Measures.Remove(html); break;
        }
        Assert.Throws<InvalidDataException>(() => NativeDashboardReadbackVerification.ValidateDefinitions(database));
    }

    private static Database Completed()
    {
        var database = SyntheticDesktopFixture.CreateDatabase();
        var changes = SyntheticDesktopFixture.ObjectChanges;
        var operation = Guid.NewGuid();
        var plan = new DefinitionPlan(operation, new("fixture", "fixture", "fixture", false), "fixture", changes.Measures)
        { Columns = changes.Columns, Relationships = changes.Relationships };
        TomObjectMutations.StageApply(database.Model, plan);
        Add(changes.Measures[0], operation);
        var metricOperation = Guid.NewGuid();
        foreach (var metric in SyntheticDesktopFixture.Dashboard.Metrics)
            Add(new(metric.Table, metric.Name, metric.Expression), metricOperation);
        Add(DashboardDesktopVerification.CompileFixtureVariant("default"), Guid.NewGuid());
        return database;

        void Add(MeasureChange change, Guid id)
        {
            var measure = new Measure { Name = change.Name, Expression = change.Expression };
            database.Model.Tables[change.Table].Measures.Add(measure);
            TomObjectMutations.MarkMeasure(measure, id);
        }
    }
}
