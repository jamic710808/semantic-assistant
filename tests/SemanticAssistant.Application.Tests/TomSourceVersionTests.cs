using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class TomSourceVersionTests
{
    private static Database Database()
    {
        var db = new Database { ID = "synthetic-version", Name = "SyntheticVersion", CompatibilityLevel = 1600, Model = new Model { Name = "Model" } };
        var table = new Table { Name = "Sales" };
        table.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Int64, SourceColumn = "Amount" });
        table.Measures.Add(new Measure { Name = "Total", Expression = "SUM('Sales'[Amount])" });
        table.Partitions.Add(new Partition { Name = "Sales", Source = new MPartitionSource { Expression = "#table({\"Amount\"}, {{1}})" } });
        db.Model.Tables.Add(table); return db;
    }
    private static ModelSnapshot Read(Database db) => TomModelProjection.Read(db, "synthetic-version");

    [Theory]
    [InlineData("partition")]
    [InlineData("source-column")]
    [InlineData("role-filter")]
    [InlineData("data-source")]
    [InlineData("hidden")]
    [InlineData("format")]
    [InlineData("lineage")]
    public void NonProjectedDefinitionChangesInvalidateIdentityWithoutExposingDefinitions(string mode)
    {
        using var db = Database(); var before = Read(db); var table = db.Model.Tables[0];
        switch (mode)
        {
            case "partition": ((MPartitionSource)table.Partitions[0].Source).Expression = "#table({\"Amount\"}, {{2}}) /* synthetic-source-marker */"; break;
            case "source-column": ((DataColumn)table.Columns[0]).SourceColumn = "OtherAmount"; break;
            case "role-filter":
                var role = new ModelRole { Name = "SyntheticRole", ModelPermission = ModelPermission.Read };
                role.TablePermissions.Add(new TablePermission { Table = table, FilterExpression = "'Sales'[Amount] > 0" });
                db.Model.Roles.Add(role); break;
            case "data-source": db.Model.DataSources.Add(new ProviderDataSource { Name = "SyntheticSource", ConnectionString = "Data Source=synthetic.invalid;Password=synthetic-secret-only" }); break;
            case "hidden": table.IsHidden = true; break;
            case "format": table.Measures[0].FormatString = "0.00"; break;
            case "lineage": table.LineageTag = Guid.NewGuid().ToString(); break;
        }
        var after = Read(db);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Tables), System.Text.Json.JsonSerializer.Serialize(after.Tables));
        Assert.NotEqual(before.Identity.Revision, after.Identity.Revision);
        Assert.Equal(before.Identity.Id, after.Identity.Id);
        var serialized = System.Text.Json.JsonSerializer.Serialize(after);
        foreach (var value in new[] { "synthetic-secret-only", "synthetic-source-marker", "SyntheticSource", "SyntheticRole", "OtherAmount" })
            Assert.DoesNotContain(value, serialized);
    }

    [Fact]
    public void RepeatedReadAndWriterUseExactlyTheSameRevision()
    {
        using var db = Database(); using var server = new Server();
        var before = Read(db); Assert.Equal(before, Read(db) with { Tables = before.Tables, Relationships = before.Relationships });
        using var connection = new TomWriterConnection(server, db, before.Identity.Id, () => { });
        var writer = connection.Read();
        Assert.Equal(before.Identity, writer.Identity); Assert.Equal(before.Identity.Revision, writer.Revision);
    }

    [Fact]
    public void SqlPartitionQueryChangesInvalidateRevisionWithoutExposingQuery()
    {
        using var db = Database(); var source = new ProviderDataSource { Name = "SyntheticSql", ConnectionString = "Data Source=synthetic.invalid" };
        db.Model.DataSources.Add(source);
        db.Model.Tables[0].Partitions[0].Source = new QueryPartitionSource { DataSource = source, Query = "SELECT Amount FROM synthetic_A" };
        var before = Read(db);
        ((QueryPartitionSource)db.Model.Tables[0].Partitions[0].Source).Query = "SELECT Amount FROM synthetic_B";
        var after = Read(db); Assert.NotEqual(before.Identity.Revision, after.Identity.Revision);
        Assert.DoesNotContain("synthetic_B", System.Text.Json.JsonSerializer.Serialize(after));
    }

    [Fact]
    public void FullDefinitionSizeLimitAlsoAppliesToReadOnlyProjection()
    {
        using var db = Database(); db.Model.Annotations.Add(new Annotation { Name = "SyntheticLarge", Value = new string('x', 8_388_609) });
        var error = Assert.Throws<InvalidDataException>(() => Read(db));
        Assert.DoesNotContain("SyntheticLarge", error.Message);
    }

    [Fact]
    public async Task RebindingPartitionInvalidatesAnAlreadyApprovedWrite()
    {
        using var db = Database(); var writer = new ProjectionWriter(db); var before = Read(db);
        var session = new PermissionSession(before.Identity, ConversationMode.Analyze, [Capability.MetadataRead, Capability.DefinitionWrite]);
        var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync([new("Sales", "SA_New", "1")], session.Snapshot, default);
        var approval = core.ApproveAfterUserConfirmation(preview, session.Snapshot);
        ((MPartitionSource)db.Model.Tables[0].Partitions[0].Source).Expression = "#table({\"Amount\"}, {{2}})";
        var result = await core.ApplyAsync(preview, approval, session.Snapshot, default);
        Assert.Equal("write_denied", result.Code); Assert.False(result.Success); Assert.False(result.PartialFailure);
        Assert.Equal(0, writer.Writes); Assert.Equal(0, writer.Restores);
    }

    private sealed class ProjectionWriter(Database database) : IModelDefinitionWriter
    {
        public int Writes, Restores;
        public Task<DefinitionState> ReadDefinitionAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); using var server = new Server();
            using var connection = new TomWriterConnection(server, database, "synthetic-version", () => { });
            return Task.FromResult(connection.Read());
        }
        public Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
        { Writes++; throw new InvalidOperationException("A stale source must not reach mutation."); }
        public Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token)
        { Restores++; throw new InvalidOperationException("A denied mutation must not restore anything."); }
    }
}
