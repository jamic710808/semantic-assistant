using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record OwnedMeasure(string Name, string Expression, string? Owner, Guid? OperationId)
{
    public bool IntegrityValid { get; init; } = true;
    public string? IntegrityStamp { get; init; }
}
public sealed record DefinitionTable(string Name, ImmutableArray<string> Columns, ImmutableArray<OwnedMeasure> Measures)
{
    public ImmutableArray<OwnedCalculatedColumn> CalculatedColumns { get; init; } = [];
    public ImmutableDictionary<string, string> ColumnTypes { get; init; } = ImmutableDictionary<string, string>.Empty;
}
public sealed record DefinitionState(ModelIdentity Identity, string Revision, ImmutableArray<DefinitionTable> Tables)
{
    public ImmutableArray<OwnedRelationship> Relationships { get; init; } = [];
    public ImmutableArray<string> UnsupportedRelationshipNames { get; init; } = [];
}
public sealed record MeasureChange([property: System.Text.Json.Serialization.JsonRequired] string Table,
    [property: System.Text.Json.Serialization.JsonRequired] string Name, [property: System.Text.Json.Serialization.JsonRequired] string Expression);
public sealed record DefinitionPlan(Guid OperationId, ModelIdentity Target, string ExpectedRevision, ImmutableArray<MeasureChange> Changes)
{
    public ImmutableArray<CalculatedColumnChange> Columns { get; init; } = [];
    public ImmutableArray<RelationshipChange> Relationships { get; init; } = [];
}
public sealed record DefinitionPreview(Guid Id, DefinitionPlan Plan, string PlanHash, DefinitionState Before, string Diff);
public sealed record WriteOutcome(string Code, bool Success, bool PartialFailure, string Message, DefinitionState? After = null);

public interface IModelDefinitionWriter
{
    Task<DefinitionState> ReadDefinitionAsync(CancellationToken token);
    Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token);
    Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token);
}

public sealed class DefinitionConflictException() : Exception("模型定義／物件所有權已改變。");
public sealed class ModelWriteApproval
{
    internal ModelWriteApproval(Guid coordinator, DefinitionPreview preview, PermissionSnapshot permissions, ModelModificationSnapshot? modification = null)
    { Coordinator = coordinator; Preview = preview; Permissions = permissions; Modification = modification; ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1); }
    internal ModelModificationSnapshot? Modification { get; }
    internal Guid Coordinator { get; }
    internal DefinitionPreview Preview { get; }
    internal PermissionSnapshot Permissions { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal int Used;
}

public static partial class DefinitionRules
{
    public const string Owner = "SemanticAssistant/v1";
    public const string OwnerAnnotation = "SemanticAssistant.Owner";
    public const string OperationAnnotation = "SemanticAssistant.OperationId";

    public static void ValidatePlan(DefinitionPlan plan, DefinitionState before)
    {
        if (plan.OperationId == Guid.Empty || plan.Target != before.Identity || plan.ExpectedRevision != before.Revision ||
            plan.Changes.IsDefault || plan.Columns.IsDefault || plan.Relationships.IsDefault ||
            ObjectCount(plan) is < 1 or > 16) throw new DefinitionConflictException();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Changes)
        {
            if (string.IsNullOrWhiteSpace(change.Table) || change.Table.Length > 128 || string.IsNullOrWhiteSpace(change.Name) ||
                change.Name.Length > 128 || change.Name.Any(char.IsControl) || !names.Add(change.Name) ||
                string.IsNullOrWhiteSpace(change.Expression) || change.Expression.Length > 8000 || change.Expression.Contains('\0'))
                throw new ToolInputException("度量值修改參數無效或重複。");
            var table = before.Tables.SingleOrDefault(t => t.Name.Equals(change.Table, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolInputException("修改目標表不存在。");
            if (table.Columns.Any(c => c.Equals(change.Name, StringComparison.OrdinalIgnoreCase))) throw new DefinitionConflictException();
            if (plan.Columns.Any(c => c.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase)))
                throw new DefinitionConflictException();
            foreach (var existing in before.Tables.SelectMany(t => t.Measures.Select(m => (Table: t.Name, Measure: m)))
                .Where(x => x.Measure.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase)))
                if (existing.Table != table.Name || existing.Measure.Owner != Owner || !existing.Measure.IntegrityValid) throw new DefinitionConflictException();
        }
        ValidateObjects(plan, before);
    }

    public static bool MatchesApplied(DefinitionPlan plan, DefinitionState state) => state.Identity.Id == plan.Target.Id &&
        plan.Changes.All(c => state.Tables.SingleOrDefault(t => t.Name.Equals(c.Table, StringComparison.OrdinalIgnoreCase))?.Measures
            .Any(m => m.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && m.Expression == c.Expression && m.Owner == Owner && m.OperationId == plan.OperationId && m.IntegrityValid) == true) && ObjectsApplied(plan, state);

    public static bool MatchesBefore(DefinitionPreview preview, DefinitionState current) => current.Identity.Id == preview.Before.Identity.Id &&
        preview.Plan.Changes.All(c => SameMeasure(Find(preview.Before, c), Find(current, c))) && ObjectsBefore(preview, current);

    public static OwnedMeasure? Find(DefinitionState state, MeasureChange change) => state.Tables
        .SingleOrDefault(t => t.Name.Equals(change.Table, StringComparison.OrdinalIgnoreCase))?.Measures
        .SingleOrDefault(m => m.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
    public static bool SameMeasure(OwnedMeasure? left, OwnedMeasure? right) => left == right;

    public static void ValidateRollback(DefinitionPreview preview, DefinitionState current)
    {
        if (current.Identity.Id != preview.Before.Identity.Id) throw new DefinitionConflictException();
        foreach (var change in preview.Plan.Changes)
        {
            var now = Find(current, change); var before = Find(preview.Before, change);
            if (SameMeasure(now, before)) continue;
            if (now is null || now.Owner != Owner || now.OperationId != preview.Plan.OperationId || now.Expression != change.Expression || !now.IntegrityValid)
                throw new DefinitionConflictException(); // Preserve edits made by another actor.
        }
        ValidateObjectRollback(preview, current);
    }
    public static string HashPlan(DefinitionPlan plan) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
}

public sealed class ModelWriteCoordinator(IModelDefinitionWriter writer, PermissionSession session, IModelWriteJournal? journal = null,
    ModelModificationPolicy? modificationPolicy = null)
{
    private readonly Guid identity = Guid.NewGuid();
    private readonly IModelDefinitionWriter guardedWriter = modificationPolicy?.Guard(writer) ?? writer;
    private readonly Dictionary<Guid, DefinitionPreview> previews = [];
    private readonly Dictionary<Guid, ModelModificationSnapshot> previewPolicies = [];
    private readonly Dictionary<Guid, (string Hash, WriteOutcome Outcome)> receipts = [];
    private readonly SemaphoreSlim serial = new(1);

    public async Task<DefinitionPreview> PrepareAsync(ImmutableArray<MeasureChange> changes, PermissionSnapshot permissions,
        CancellationToken token, Guid? operationId = null)
        => await PrepareAsync(new ModelChanges(changes, [], []), permissions, token, operationId);

    public async Task<DefinitionPreview> PrepareAsync(ModelChanges changes, PermissionSnapshot permissions,
        CancellationToken token, Guid? operationId = null)
    {
        var epoch = modificationPolicy?.Snapshot;
        CheckPermission(permissions, Capability.MetadataRead); token.ThrowIfCancellationRequested();
        var before = await guardedWriter.ReadDefinitionAsync(token).WaitAsync(token);
        CheckPermission(permissions, Capability.MetadataRead);
        if (before.Identity != permissions.Target) throw new DefinitionConflictException();
        var plan = new DefinitionPlan(operationId ?? Guid.NewGuid(), before.Identity, before.Revision, changes.Measures)
        { Columns = changes.Columns, Relationships = changes.Relationships };
        DefinitionRules.ValidatePlan(plan, before);
        var diff = new StringBuilder($"目標：{before.Identity.Name}\n完整定義版本：{before.Revision}\n操作：{plan.OperationId}\n");
        foreach (var change in changes.Measures)
        {
            var prior = DefinitionRules.Find(before, change);
            diff.Append($"\n{(prior is null ? "新增" : "更新自有物件")} {change.Table}[{change.Name}]\n- {prior?.Expression ?? "（不存在）"}\n+ {change.Expression}\n");
        }
        DefinitionRules.AppendObjectDiff(plan, before, diff);
        var preview = new DefinitionPreview(Guid.NewGuid(), plan, DefinitionRules.HashPlan(plan), before, diff.ToString());
        if (epoch is not null) modificationPolicy!.EnsureCurrent(epoch);
        lock (previews)
        {
            if (previews.Count >= 128) { previews.Clear(); previewPolicies.Clear(); }
            previews.Add(preview.Id, preview);
            if (epoch is not null) previewPolicies.Add(preview.Id, epoch);
        }
        return preview;
    }

    public ModelWriteApproval ApproveAfterUserConfirmation(DefinitionPreview preview, PermissionSnapshot permissions)
    {
        CheckPermission(permissions, Capability.DefinitionWrite);
        lock (previews) if (!previews.TryGetValue(preview.Id, out var issued) || issued != preview || preview.Plan.Target != permissions.Target ||
            preview.PlanHash != DefinitionRules.HashPlan(preview.Plan)) throw new UnauthorizedAccessException("預覽已變更。");
        ModelModificationSnapshot? epoch = null;
        if (modificationPolicy is not null)
        {
            lock (previews) epoch = previewPolicies.TryGetValue(preview.Id, out var issuedEpoch) ? issuedEpoch : throw new ModelModificationDeniedException();
            modificationPolicy.EnsureAllowed(epoch);
        }
        return new(identity, preview, permissions, epoch);
    }

    public async Task<WriteOutcome> ApplyAsync(DefinitionPreview preview, ModelWriteApproval? approval,
        PermissionSnapshot permissions, CancellationToken token)
    {
        await serial.WaitAsync(token);
        bool attempted = false;
        IModelWriteAudit? audit = null;
        void EnsureWriteAuthorization()
        {
            CheckPermission(permissions, Capability.DefinitionWrite);
            if (modificationPolicy is not null) modificationPolicy.EnsureAllowed(approval?.Modification ?? throw new ModelModificationDeniedException());
        }
        try
        {
            EnsureWriteAuthorization();
            if (approval is null || approval.Coordinator != identity || approval.Preview != preview || approval.Permissions != permissions ||
                approval.ExpiresAt <= DateTimeOffset.UtcNow || preview.PlanHash != DefinitionRules.HashPlan(preview.Plan))
                return new("approval_denied", false, false, "缺少符合本次預覽、目標與參數的逐次同意。");
            if (receipts.TryGetValue(preview.Plan.OperationId, out var receipt))
            {
                if (receipt.Hash != preview.PlanHash) return new("operation_collision", false, false, "操作 ID 已用於不同參數。");
                var current = await guardedWriter.ReadDefinitionAsync(token).WaitAsync(token);
                EnsureWriteAuthorization();
                return receipt.Outcome.Success && DefinitionRules.MatchesApplied(preview.Plan, current)
                    ? receipt.Outcome with { Code = "already_applied", After = current }
                    : new("receipt_stale", false, true, "先前操作結果已變更或失敗；沒有自動重試。");
            }
            if (Interlocked.Exchange(ref approval.Used, 1) != 0) return new("approval_used", false, false, "當次同意已使用。");
            var before = await guardedWriter.ReadDefinitionAsync(token).WaitAsync(token);
            EnsureWriteAuthorization(); token.ThrowIfCancellationRequested();
            DefinitionRules.ValidatePlan(preview.Plan, before);
            if (receipts.Count >= 256) return new("operation_limit", false, false, "本次工作階段操作已達上限；沒有開始寫入。");
            audit = journal?.Begin(preview);
            attempted = true;
            await guardedWriter.ApplyAsync(preview.Plan, () => { EnsureWriteAuthorization(); token.ThrowIfCancellationRequested(); }, token);
            EnsureWriteAuthorization(); token.ThrowIfCancellationRequested();
            var after = await guardedWriter.ReadDefinitionAsync(token).WaitAsync(token);
            EnsureWriteAuthorization();
            if (!DefinitionRules.MatchesApplied(preview.Plan, after)) throw new DefinitionConflictException();
            var success = new WriteOutcome("ok", true, false, "修改已回讀驗證；僅處理本程式擁有的模型物件。", after);
            receipts[preview.Plan.OperationId] = (preview.PlanHash, success);
            if (!RecordAudit(audit, "verified")) return success with { Code = "verified_journal_incomplete", Message = "修改已回讀驗證；稽核結束紀錄未能寫入，請以操作 ID 核對。" };
            return success;
        }
        catch (Exception) when (!attempted)
        { return new("write_denied", false, false, "授權、定義版本、物件所有權或稽核 intent 不符；沒有開始寫入。"); }
        catch (Exception)
        {
            // Repair only this operation's owned objects, with a separate bounded token after cancellation.
            using var repair = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RecordAudit(audit, "rollback_started");
            WriteOutcome outcome;
            try
            {
                var current = await guardedWriter.ReadDefinitionAsync(repair.Token).WaitAsync(repair.Token);
                DefinitionRules.ValidateRollback(preview, current);
                // Do not detach a native restore that could still be saving after the UI closes.
                if (!DefinitionRules.MatchesBefore(preview, current))
                    await guardedWriter.RestoreAsync(preview, current, repair.Token);
                var restored = await guardedWriter.ReadDefinitionAsync(repair.Token).WaitAsync(repair.Token);
                if (!DefinitionRules.MatchesBefore(preview, restored)) throw new DefinitionConflictException();
                outcome = new("failed_rolled_back", false, false, "寫入或驗證失敗；當次自有物件已回讀確認還原。", restored);
            }
            catch
            { outcome = new("partial_failure", false, true, $"寫入／還原未能驗證。請重新讀取模型，比對操作 {preview.Plan.OperationId} 的自有物件；勿直接重試。"); }
            receipts[preview.Plan.OperationId] = (preview.PlanHash, outcome);
            if (!RecordAudit(audit, outcome.Code)) outcome = outcome with { Message = outcome.Message + " 稽核結束紀錄未能寫入。" };
            return outcome;
        }
        finally { try { audit?.Dispose(); } catch { } serial.Release(); }
    }
    private static bool RecordAudit(IModelWriteAudit? audit, string code)
    { try { audit?.Record(code); return true; } catch { return false; } }
    private void CheckPermission(PermissionSnapshot permissions, Capability capability)
    { if (!session.IsCurrent(permissions) || !permissions.Allows(capability)) throw new UnauthorizedAccessException("寫入授權失效。"); }
}
