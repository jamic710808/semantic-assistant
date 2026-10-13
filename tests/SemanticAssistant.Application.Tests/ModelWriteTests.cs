using System.Collections.Immutable;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ModelWriteTests
{
    private static PermissionSession Session(bool write = true) => new(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
        write ? ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite) : ImmutableHashSet.Create(Capability.MetadataRead));
    private static ImmutableArray<MeasureChange> Changes => [new("銷售", "SA_合成銷售", "SUM('銷售'[金額])")];

    [Fact]
    public async Task DefaultInstallationCannotWriteWithoutBothCapabilityAndApproval()
    {
        var writer = new ProbeWriter(); var session = Session(false); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(Changes, session.Snapshot, default);
        Assert.Throws<UnauthorizedAccessException>(() => core.ApproveAfterUserConfirmation(preview, session.Snapshot));
        Assert.False((await core.ApplyAsync(preview, null, session.Snapshot, default)).Success); Assert.Equal(0, writer.Writes);
        var granted = Session(); core = new(writer, granted); preview = await core.PrepareAsync(Changes, granted.Snapshot, default);
        Assert.Equal("approval_denied", (await core.ApplyAsync(preview, null, granted.Snapshot, default)).Code); Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task SuccessfulOwnedWriteIsReadBackAndIdempotent()
    {
        var writer = new ProbeWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(Changes, session.Snapshot, default);
        Assert.Contains("新增", preview.Diff);
        var approval = core.ApproveAfterUserConfirmation(preview, session.Snapshot);
        var result = await core.ApplyAsync(preview, approval, session.Snapshot, default);
        Assert.True(result.Success); Assert.False(result.PartialFailure); Assert.Equal(DefinitionRules.Owner, DefinitionRules.Find(result.After!, Changes[0])!.Owner);
        Assert.Equal("already_applied", (await core.ApplyAsync(preview, approval, session.Snapshot, default)).Code); Assert.Equal(1, writer.Writes);
    }

    [Fact]
    public async Task ExistingUnownedOrOtherTableMeasureCollisionIsRejected()
    {
        var writer = new ProbeWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        await Assert.ThrowsAsync<DefinitionConflictException>(() => core.PrepareAsync([new("銷售", "銷售額", "1")], session.Snapshot, default));
        await Assert.ThrowsAsync<DefinitionConflictException>(() => core.PrepareAsync([new("日期", "銷售額", "1")], session.Snapshot, default));
        Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task DefinitionRevisionConflictStopsBeforeMutation()
    {
        var writer = new ProbeWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(Changes, session.Snapshot, default);
        var approval = core.ApproveAfterUserConfirmation(preview, session.Snapshot); writer.RevisionOverride = "other-definition";
        Assert.Equal("write_denied", (await core.ApplyAsync(preview, approval, session.Snapshot, default)).Code); Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task ChangedArgsOrRevokedSessionCannotUseApproval()
    {
        var writer = new ProbeWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session); var pinned = session.Snapshot;
        var preview = await core.PrepareAsync(Changes, pinned, default); var approval = core.ApproveAfterUserConfirmation(preview, pinned);
        var forgedPlan = preview.Plan with { Changes = [new("銷售", "SA_合成銷售", "2")] };
        var forged = preview with { Plan = forgedPlan, PlanHash = DefinitionRules.HashPlan(forgedPlan) };
        Assert.Equal("approval_denied", (await core.ApplyAsync(forged, approval, pinned, default)).Code);
        session.Update(pinned.Target, pinned.Mode, pinned.Granted);
        Assert.Equal("write_denied", (await core.ApplyAsync(preview, approval, pinned, default)).Code); Assert.Equal(0, writer.Writes);
    }

    [Fact]
    public async Task VerificationFailureTriggersVerifiedRollback()
    {
        var writer = new ProbeWriter { FailFirstVerification = true }; var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(Changes, session.Snapshot, default); var approval = core.ApproveAfterUserConfirmation(preview, session.Snapshot);
        var outcome = await core.ApplyAsync(preview, approval, session.Snapshot, default);
        Assert.Equal("failed_rolled_back", outcome.Code); Assert.False(outcome.Success); Assert.False(outcome.PartialFailure);
        Assert.Equal(1, writer.Rollbacks); Assert.True(DefinitionRules.MatchesBefore(preview, await writer.ReadDefinitionAsync(default)));
    }

    [Fact]
    public async Task RollbackFailureIsVisibleAndNeverReportedAsSuccess()
    {
        var writer = new ProbeWriter { FailFirstVerification = true, FailRollback = true }; var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(Changes, session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default);
        Assert.Equal("partial_failure", outcome.Code); Assert.True(outcome.PartialFailure); Assert.False(outcome.Success);
        Assert.Contains(preview.Plan.OperationId.ToString(), outcome.Message); Assert.DoesNotContain("synthetic-secret", outcome.Message);
    }

    [Fact]
    public async Task CancelAfterCommitRepairsWithSeparateBoundedToken()
    {
        using var cancel = new CancellationTokenSource(); var writer = new ProbeWriter { AfterCommit = cancel.Cancel };
        var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var preview = await core.PrepareAsync(Changes, session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, cancel.Token);
        Assert.Equal("failed_rolled_back", outcome.Code); Assert.Equal(1, writer.Rollbacks);
    }

    [Fact]
    public async Task OwnedMeasureCanBeUpdatedAndPreservesOwnership()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var first = await core.PrepareAsync(Changes, session.Snapshot, default);
        var result = await core.ApplyAsync(first, core.ApproveAfterUserConfirmation(first, session.Snapshot), session.Snapshot, default);
        session.Update(result.After!.Identity, ConversationMode.Analyze, session.Snapshot.Granted);
        var second = await core.PrepareAsync([new("銷售", "SA_合成銷售", "2")], session.Snapshot, default);
        Assert.Contains("更新自有物件", second.Diff);
        Assert.True((await core.ApplyAsync(second, core.ApproveAfterUserConfirmation(second, session.Snapshot), session.Snapshot, default)).Success);
    }

    [Fact]
    public async Task SameOperationIdWithDifferentPlanIsCollision()
    {
        var writer = new ProbeWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session);
        var first = await core.PrepareAsync(Changes, session.Snapshot, default);
        var result = await core.ApplyAsync(first, core.ApproveAfterUserConfirmation(first, session.Snapshot), session.Snapshot, default);
        session.Update(result.After!.Identity, ConversationMode.Analyze, session.Snapshot.Granted);
        var second = await core.PrepareAsync([new("銷售", "SA_合成銷售", "2")], session.Snapshot, default, first.Plan.OperationId);
        Assert.Equal("operation_collision", (await core.ApplyAsync(second, core.ApproveAfterUserConfirmation(second, session.Snapshot), session.Snapshot, default)).Code);
        Assert.Equal(1, writer.Writes);
    }

    private sealed class ProbeWriter : IModelDefinitionWriter
    {
        private readonly DemoDefinitionWriter inner = new();
        public int Writes, Rollbacks;
        public bool FailFirstVerification, FailRollback;
        public string? RevisionOverride;
        public Action? AfterCommit;
        private bool failNext;
        public async Task<DefinitionState> ReadDefinitionAsync(CancellationToken token)
        {
            var state = await inner.ReadDefinitionAsync(token);
            if (RevisionOverride is not null) return state with { Revision = RevisionOverride };
            if (failNext) { failNext = false; return state with { Tables = [] }; }
            return state;
        }
        public async Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
        { Writes++; await inner.ApplyAsync(plan, ensureAuthorized, token); failNext = FailFirstVerification; AfterCommit?.Invoke(); }
        public async Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token)
        { Rollbacks++; if (FailRollback) throw new IOException("synthetic-secret-connection"); await inner.RestoreAsync(preview, observed, token); }
    }
}
