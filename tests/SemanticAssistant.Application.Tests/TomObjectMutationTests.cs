using System.Collections.Immutable;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class TomObjectMutationTests
{
    [Fact]
    public void LineageIsAssignedBeforeSealingAndIdentityChangesRemainConflicts()
    {
        using var db = Database();
        var measure = new Measure { Name = "SA_identity", Expression = "1" };
        db.Model.Tables[0].Measures.Add(measure);
        TomObjectMutations.MarkMeasure(measure, Guid.NewGuid());
        Assert.True(Guid.TryParse(measure.LineageTag, out _));
        var original = measure.LineageTag;
        TomObjectMutations.MarkMeasure(measure, Guid.NewGuid());
        Assert.Equal(original, measure.LineageTag);
        Assert.True(TomObjectMutations.ReadMeasure(measure).IntegrityValid);
        measure.LineageTag = Guid.NewGuid().ToString();
        Assert.False(TomObjectMutations.ReadMeasure(measure).IntegrityValid);
        var plan = Plan(Read(db));
        TomObjectMutations.StageApply(db.Model, plan);
        var column = Assert.IsType<CalculatedColumn>(db.Model.Tables[0].Columns[1]);
        Assert.True(Guid.TryParse(column.LineageTag, out _));
        Assert.True(TomObjectMutations.ReadColumn(column).IntegrityValid);
        column.LineageTag = Guid.NewGuid().ToString();
        Assert.False(TomObjectMutations.ReadColumn(column).IntegrityValid);
    }
    [Fact]
    public void ServerInferredPropertiesDoNotBreakDefinitionIntegrity()
    {
        using var database = SemanticAssistant.PowerBIFixture.SyntheticDesktopFixture.CreateDatabase();
        var table = database.Model.Tables["Sales"];
        var measure = new Measure { Name = "SA_inferred", Expression = "SUM('Sales'[Revenue amount])" };
        table.Measures.Add(measure);
        TomObjectMutations.MarkMeasure(measure, Guid.NewGuid());
        var stamp = TomObjectMutations.ReadMeasure(measure).IntegrityStamp;
        Assert.StartsWith("definition-v2:", stamp);
        var json = System.Text.Json.Nodes.JsonNode.Parse(Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeObject(measure))!;
        json["dataType"] = "decimal";
        json["modifiedTime"] = "2026-10-12T21:45:20.306667";
        json["structureModifiedTime"] = "2026-10-12T21:45:18.613333";
        foreach (var annotation in json["annotations"]!.AsArray()) annotation!["modifiedTime"] = "2026-10-12T21:45:18.63";
        var readBack = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeObject<Measure>(json.ToJsonString());
        Assert.True(TomObjectMutations.ReadMeasure(readBack).IntegrityValid);
        readBack.FormatString = "0.00";
        Assert.False(TomObjectMutations.ReadMeasure(readBack).IntegrityValid);
    }
    private static Database Database()
    {
        var db = new Database { ID = "synthetic-object-mutations", Name = "SyntheticObjects", CompatibilityLevel = 1600, Model = new Model { Name = "Model" } };
        var sales = new Table { Name = "銷售" };
        sales.Columns.Add(new DataColumn { Name = "日期", DataType = DataType.DateTime, SourceColumn = "Date" });
        var dates = new Table { Name = "日期" }; dates.Columns.Add(new DataColumn { Name = "月份", DataType = DataType.String, SourceColumn = "Month" });
        db.Model.Tables.Add(sales); db.Model.Tables.Add(dates); return db;
    }
    private static DefinitionState Read(Database db)
    {
        using var connection = new TomWriterConnection(new Server(), db, "synthetic-native-stage", () => { }); return connection.Read();
    }
    private static DefinitionPlan Plan(DefinitionState before) => new(Guid.NewGuid(), before.Identity, before.Revision, [])
    { Columns = DemoModelChanges.Mixed.Columns, Relationships = DemoModelChanges.Mixed.Relationships };
    [Fact]
    public void OfficialTomObjectsStageReadBackAndRestoreWithoutServerConnectOrSaveChanges()
    {
        using var db = Database(); var before = Read(db); var plan = Plan(before); DefinitionRules.ValidatePlan(plan, before);
        TomObjectMutations.StageApply(db.Model, plan); var after = Read(db);
        Assert.True(DefinitionRules.MatchesApplied(plan, after)); Assert.True(after.Tables[0].CalculatedColumns[0].IntegrityValid);
        Assert.True(after.Relationships[0].IntegrityValid);
        var preview = new DefinitionPreview(Guid.NewGuid(), plan, DefinitionRules.HashPlan(plan), before, "synthetic");
        DefinitionRules.ValidateRollback(preview, after); TomObjectMutations.StageRestore(db.Model, preview, after);
        Assert.True(DefinitionRules.MatchesBefore(preview, Read(db))); Assert.Empty(db.Model.Relationships);
    }
    [Theory]
    [InlineData("column-extra")]
    [InlineData("column-dataType")]
    [InlineData("relationship-security")]
    public void ExternalPropertiesInvalidateIntegrityAndPreserveExternalChanges(string kind)
    {
        using var db = Database(); var before = Read(db); var plan = Plan(before); TomObjectMutations.StageApply(db.Model, plan);
        if (kind == "column-extra") db.Model.Tables[0].Columns[1].Annotations.Add(new Annotation { Name = "ForeignActor", Value = "preserve" });
        else if (kind == "column-dataType") db.Model.Tables[0].Columns[1].DataType = DataType.Int64;
        else db.Model.Relationships[0].SecurityFilteringBehavior = SecurityFilteringBehavior.BothDirections;
        var after = Read(db); Assert.False(DefinitionRules.MatchesApplied(plan, after));
        var preview = new DefinitionPreview(Guid.NewGuid(), plan, DefinitionRules.HashPlan(plan), before, "synthetic");
        Assert.Throws<DefinitionConflictException>(() => DefinitionRules.ValidateRollback(preview, after));
        Assert.Single(db.Model.Relationships); Assert.Equal(2, db.Model.Tables[0].Columns.Count);
    }
    [Fact]
    public void UpdateAndRestorePreserveOriginalIntegrityAndAnnotations()
    {
        using var db = Database(); var firstPlan = Plan(Read(db)); TomObjectMutations.StageApply(db.Model, firstPlan);
        var before = Read(db); var second = Plan(before) with
        { Columns = [firstPlan.Columns[0] with { Expression = "\"other\"" }], Relationships = [firstPlan.Relationships[0] with { IsActive = true }] };
        DefinitionRules.ValidatePlan(second, before); TomObjectMutations.StageApply(db.Model, second); var after = Read(db);
        Assert.True(DefinitionRules.MatchesApplied(second, after));
        var preview = new DefinitionPreview(Guid.NewGuid(), second, DefinitionRules.HashPlan(second), before, "synthetic");
        DefinitionRules.ValidateRollback(preview, after); TomObjectMutations.StageRestore(db.Model, preview, after);
        Assert.True(DefinitionRules.MatchesBefore(preview, Read(db))); Assert.True(Read(db).Relationships[0].IntegrityValid);
    }
    [Fact]
    public void ForeignHierarchyDependencyIsDetectedBeforeStagingAnyRemoval()
    {
        using var db = Database(); var before = Read(db); var plan = Plan(before); TomObjectMutations.StageApply(db.Model, plan);
        var hierarchy = new Hierarchy { Name = "ForeignHierarchy" };
        hierarchy.Levels.Add(new Level { Name = "ForeignLevel", Ordinal = 0, Column = db.Model.Tables[0].Columns[1] });
        db.Model.Tables[0].Hierarchies.Add(hierarchy);
        var preview = new DefinitionPreview(Guid.NewGuid(), plan, DefinitionRules.HashPlan(plan), before, "synthetic");
        Assert.Throws<DefinitionConflictException>(() => TomObjectMutations.StageRestore(db.Model, preview, Read(db)));
        Assert.Single(db.Model.Relationships); Assert.Equal(2, db.Model.Tables[0].Columns.Count); Assert.Single(db.Model.Tables[0].Hierarchies);
    }
    [Fact]
    public void OwnedMeasureIntegrityAlsoDetectsForeignFormattingEdits()
    {
        using var db = Database(); var measure = new Measure { Name = "SA_own", Expression = "1" }; db.Model.Tables[0].Measures.Add(measure);
        TomObjectMutations.MarkMeasure(measure, Guid.NewGuid()); Assert.True(TomObjectMutations.ReadMeasure(measure).IntegrityValid);
        measure.FormatString = "0.00"; Assert.False(TomObjectMutations.ReadMeasure(measure).IntegrityValid);
    }
    [Fact]
    public void AnnotationOrderDoesNotInvalidateObjectIntegrity()
    {
        using var db = Database(); var measure = new Measure { Name = "SA_order", Expression = "1" }; db.Model.Tables[0].Measures.Add(measure);
        TomObjectMutations.MarkMeasure(measure, Guid.NewGuid());
        var owner = measure.Annotations.Find(DefinitionRules.OwnerAnnotation)!;
        var value = owner.Value; measure.Annotations.Remove(owner);
        measure.Annotations.Add(new Annotation { Name = DefinitionRules.OwnerAnnotation, Value = value });
        Assert.True(TomObjectMutations.ReadMeasure(measure).IntegrityValid);
    }
}
