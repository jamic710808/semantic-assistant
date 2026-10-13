using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class TomWriterProjectionTests
{
    private static Database Database()
    {
        var db = new Database { ID = "synthetic-tom", Name = "SyntheticTom", CompatibilityLevel = 1600, Model = new Model { Name = "Model" } };
        var table = new Table { Name = "Sales" };
        table.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Decimal, SourceColumn = "Amount" });
        table.Measures.Add(new Measure { Name = "SA_Total", Expression = "SUM('Sales'[Amount])" });
        db.Model.Tables.Add(table); return db;
    }
    [Fact]
    public void FullDefinitionDetectsChangesOutsideTheReadOnlyProjection()
    {
        using var db = Database(); using var server = new Server();
        using var connection = new TomWriterConnection(server, db, "synthetic-projection", () => { });
        var first = connection.Read();
        db.Model.Tables[0].Measures[0].Annotations.Add(new Annotation { Name = DefinitionRules.OwnerAnnotation, Value = DefinitionRules.Owner });
        var second = connection.Read();
        Assert.Equal(first.Identity, second.Identity); Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(DefinitionRules.Owner, second.Tables[0].Measures[0].Owner);
        var operation = Guid.NewGuid();
        db.Model.Tables[0].Measures[0].Annotations.Add(new Annotation { Name = DefinitionRules.OperationAnnotation, Value = operation.ToString() });
        Assert.Equal(operation, connection.Read().Tables[0].Measures[0].OperationId);
    }
    [Fact]
    public void SourceGuardRunsBeforeDefinitionReadWithoutConnectingToAnyServer()
    {
        using var db = Database(); using var server = new Server();
        using var connection = new TomWriterConnection(server, db, "synthetic-projection", () => throw new DefinitionConflictException());
        Assert.Throws<DefinitionConflictException>(() => connection.Read());
    }
    [Fact]
    public async Task AuthorizationIsCheckedBeforeOpeningAWriteConnection()
    {
        var opened = 0;
        var writer = new TomDefinitionWriter(_ => { opened++; return Task.FromException<TomWriterConnection>(new IOException()); });
        var model = SemanticAssistant.Adapters.Demo.DemoModel.Snapshot.Identity;
        var plan = new DefinitionPlan(Guid.NewGuid(), model, "unused", [new("Sales", "SA_Total", "1")]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.ApplyAsync(plan, () => throw new UnauthorizedAccessException(), default));
        Assert.Equal(0, opened);
    }
}
