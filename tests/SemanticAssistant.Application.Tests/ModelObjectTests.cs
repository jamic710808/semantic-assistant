using System.Collections.Immutable;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ModelObjectTests
{
    private static PermissionSession Session(bool write = true) => new(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
        write ? ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite) : ImmutableHashSet.Create(Capability.MetadataRead));
    [Fact]
    public async Task MixedPreviewIsReadOnlyAndWritesAllThreeTypesOnlyAfterApproval()
    {
        var writer = new Probe(); var session = Session(false); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(DemoModelChanges.Mixed, session.Snapshot, default);
        Assert.Contains("計算欄位", preview.Diff); Assert.Contains("交叉篩選", preview.Diff); Assert.Equal(3, DefinitionRules.ObjectCount(preview.Plan));
        Assert.Equal(0, writer.Writes); Assert.Throws<UnauthorizedAccessException>(() => core.ApproveAfterUserConfirmation(preview, session.Snapshot));
        session.Update(session.Snapshot.Target, session.Snapshot.Mode, session.Snapshot.Granted.Add(Capability.DefinitionWrite));
        Assert.False((await core.ApplyAsync(preview, null, session.Snapshot, default)).Success);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default);
        Assert.True(outcome.Success); Assert.True(DefinitionRules.MatchesApplied(preview.Plan, outcome.After!));
        Assert.Single(outcome.After!.Relationships); Assert.False(outcome.After.Relationships[0].Definition.IsActive);
        Assert.Single(outcome.After.Tables[0].CalculatedColumns); Assert.Equal(1, writer.Writes);
    }
    [Fact]
    public async Task MixedVerificationFailureRollsBackMeasuresColumnsAndRelationships()
    {
        var writer = new Probe { FailVerification = true }; var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(DemoModelChanges.Mixed, session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default);
        Assert.Equal("failed_rolled_back", outcome.Code); Assert.Equal(1, writer.Restores);
        Assert.True(DefinitionRules.MatchesBefore(preview, await writer.ReadDefinitionAsync(default)));
        Assert.Equal(2, outcome.After!.Tables[0].Columns.Length); Assert.Empty(outcome.After.Relationships);
    }
    [Theory]
    [InlineData("base-column")]
    [InlineData("measure-column")]
    [InlineData("mismatched-type")]
    [InlineData("missing-field")]
    [InlineData("same-table")]
    [InlineData("duplicate-column")]
    [InlineData("invalid-enum")]
    public async Task InvalidObjectPlansAreDeniedBeforeAnyWrite(string scenario)
    {
        var mixed = DemoModelChanges.Mixed;
        var changes = scenario switch
        {
            "base-column" => mixed with { Columns = [mixed.Columns[0] with { Name = "日期" }] },
            "measure-column" => mixed with { Columns = [mixed.Columns[0] with { Name = "銷售額" }] },
            "mismatched-type" => mixed with { Relationships = [mixed.Relationships[0] with { From = new("銷售", "金額") }] },
            "missing-field" => mixed with { Relationships = [mixed.Relationships[0] with { To = new("日期", "missing") }] },
            "same-table" => mixed with { Relationships = [mixed.Relationships[0] with { To = new("銷售", "日期") }] },
            "duplicate-column" => mixed with { Columns = [mixed.Columns[0], mixed.Columns[0]] },
            _ => mixed with { Columns = [mixed.Columns[0] with { DataType = (ColumnValueType)99 }] }
        };
        var writer = new Probe(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        await Assert.ThrowsAnyAsync<Exception>(() => core.PrepareAsync(changes, session.Snapshot, default)); Assert.Equal(0, writer.Writes);
    }
    [Fact]
    public async Task ChangedTypeAndDuplicateEndpointsAreRejectedForOwnedObjects()
    {
        var writer = new Probe(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var first = await core.PrepareAsync(DemoModelChanges.Mixed, session.Snapshot, default);
        var outcome = await core.ApplyAsync(first, core.ApproveAfterUserConfirmation(first, session.Snapshot), session.Snapshot, default);
        session.Update(outcome.After!.Identity, session.Snapshot.Mode, session.Snapshot.Granted);
        await Assert.ThrowsAsync<DefinitionConflictException>(() => core.PrepareAsync(DemoModelChanges.Mixed with
        { Columns = [DemoModelChanges.Mixed.Columns[0] with { DataType = ColumnValueType.Int64 }] }, session.Snapshot, default));
        await Assert.ThrowsAsync<DefinitionConflictException>(() => core.PrepareAsync(new ModelChanges([], [],
            [DemoModelChanges.Mixed.Relationships[0] with { Name = "SA_same_endpoints" }]), session.Snapshot, default));
    }
    [Fact]
    public async Task OwnedObjectUpdatesCanRestoreTheirPreviousExpressionsAndRelationshipSettings()
    {
        var writer = new Probe(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var first = await core.PrepareAsync(DemoModelChanges.Mixed, session.Snapshot, default);
        var outcome = await core.ApplyAsync(first, core.ApproveAfterUserConfirmation(first, session.Snapshot), session.Snapshot, default);
        session.Update(outcome.After!.Identity, session.Snapshot.Mode, session.Snapshot.Granted);
        var update = DemoModelChanges.Mixed with
        {
            Columns = [DemoModelChanges.Mixed.Columns[0] with { Expression = "\"constant\"" }],
            Relationships = [DemoModelChanges.Mixed.Relationships[0] with { IsActive = true, CrossFiltering = RelationshipFilter.BothDirections }]
        };
        var second = await core.PrepareAsync(update, session.Snapshot, default); writer.FailVerification = true;
        var failed = await core.ApplyAsync(second, core.ApproveAfterUserConfirmation(second, session.Snapshot), session.Snapshot, default);
        Assert.Equal("failed_rolled_back", failed.Code); Assert.True(DefinitionRules.MatchesBefore(second, failed.After!));
        Assert.False(failed.After!.Relationships[0].Definition.IsActive);
    }
    [Fact]
    public async Task ExternalChangeToOwnedObjectsPreventsDestructiveRollback()
    {
        var writer = new Probe { MarkExternalChange = true }; var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(DemoModelChanges.Mixed, session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default);
        Assert.Equal("partial_failure", outcome.Code); Assert.True(outcome.PartialFailure); Assert.Equal(0, writer.Restores);
    }
    [Fact]
    public void TypedObjectJsonRejectsMissingOrNumericEnumsAndDuplicateFields()
    {
        Assert.ThrowsAny<Exception>(() => StrictJson.Parse<ModelChanges>("{\"Measures\":[],\"Columns\":[]}"));
        Assert.ThrowsAny<Exception>(() => StrictJson.Parse<ModelChanges>("{\"Measures\":[],\"Columns\":[],\"Relationships\":[],\"Columns\":[]}"));
        Assert.ThrowsAny<Exception>(() => StrictJson.Parse<CalculatedColumnChange>("{\"Table\":\"銷售\",\"Name\":\"SA_x\",\"Expression\":\"1\",\"DataType\":1}"));
    }
    [Fact]
    public async Task StandaloneOwnedColumnCanChangeTypeWithPreviewAndReadback()
    {
        var writer = new Probe(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var first = await core.PrepareAsync(new ModelChanges([], [new("銷售", "SA_type", "1", ColumnValueType.Int64)], []), session.Snapshot, default);
        var outcome = await core.ApplyAsync(first, core.ApproveAfterUserConfirmation(first, session.Snapshot), session.Snapshot, default);
        session.Update(outcome.After!.Identity, session.Snapshot.Mode, session.Snapshot.Granted);
        var next = await core.PrepareAsync(new ModelChanges([], [new("銷售", "SA_type", "\"one\"", ColumnValueType.String)], []), session.Snapshot, default);
        var changed = await core.ApplyAsync(next, core.ApproveAfterUserConfirmation(next, session.Snapshot), session.Snapshot, default);
        Assert.True(changed.Success); Assert.Equal("String", changed.After!.Tables[0].ColumnTypes["SA_type"]);
    }
    [Fact]
    public async Task UnsupportedRelationshipTypeCannotBeShadowedBySameName()
    {
        var writer = new DemoDefinitionWriter(); var before = await writer.ReadDefinitionAsync(default);
        before = before with { UnsupportedRelationshipNames = [DemoModelChanges.Mixed.Relationships[0].Name] };
        var plan = new DefinitionPlan(Guid.NewGuid(), before.Identity, before.Revision, [])
        { Columns = DemoModelChanges.Mixed.Columns, Relationships = DemoModelChanges.Mixed.Relationships };
        Assert.Throws<DefinitionConflictException>(() => DefinitionRules.ValidatePlan(plan, before));
    }
    [Fact]
    public async Task ReadOnlyTreeShowsCalculatedExpressionAndActualRelationshipSettings()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(DemoModelChanges.Mixed, session.Snapshot, default);
        Assert.True((await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default)).Success);
        var snapshot = await writer.ReadSnapshotAsync(default);
        var column = snapshot.Tables[0].Columns.Single(c => c.Name == "SA_月份鍵"); Assert.Contains("FORMAT", column.Expression);
        var nodes = MetadataInspector.Build(snapshot, "SA_月份鍵");
        static IEnumerable<MetadataNode> Flatten(IEnumerable<MetadataNode> values) => values.SelectMany(v => new[] { v }.Concat(Flatten(v.Children)));
        Assert.Contains(Flatten(nodes), n => n.Detail.Contains("計算欄位 DAX") && n.Detail.Contains("FORMAT"));
        var relationship = Flatten(MetadataInspector.Build(snapshot)).Single(n => n.Id.StartsWith("relationship/"));
        Assert.Contains("OneDirection", relationship.Detail); Assert.Contains("Many", relationship.Detail);
    }
    private sealed class Probe : IModelDefinitionWriter
    {
        private readonly DemoDefinitionWriter inner = new();
        public int Writes, Restores;
        public bool FailVerification, MarkExternalChange;
        private bool failNext, committed;
        public async Task<DefinitionState> ReadDefinitionAsync(CancellationToken token)
        {
            var state = await inner.ReadDefinitionAsync(token);
            if (committed && MarkExternalChange)
                return state with { Relationships = state.Relationships.Select(r => r with { IntegrityValid = false }).ToImmutableArray() };
            if (failNext) { failNext = false; return state with { Relationships = [] }; }
            return state;
        }
        public async Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
        { Writes++; await inner.ApplyAsync(plan, ensureAuthorized, token); committed = true; failNext = FailVerification; }
        public async Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token)
        { Restores++; await inner.RestoreAsync(preview, observed, token); }
    }
}
