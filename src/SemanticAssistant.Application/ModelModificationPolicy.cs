using System.Collections.Concurrent;

namespace SemanticAssistant.Application;

public sealed record ModelModificationSnapshot(Guid PolicyId, long Version, bool Allowed);
public sealed class ModelModificationDeniedException() : UnauthorizedAccessException("安全模式已禁止修改資料模型，或原寫入政策已失效。");

// One host-wide policy. Switching off and on never revives an earlier epoch.
public sealed class ModelModificationPolicy(bool allowed = false)
{
    private readonly object sync = new();
    private ModelModificationSnapshot current = new(Guid.NewGuid(), 1, allowed);
    public event Action? Changed;
    public ModelModificationSnapshot Snapshot { get { lock (sync) return current; } }
    public void Update(bool allowed)
    {
        lock (sync)
        {
            if (current.Allowed == allowed) return;
            current = current with { Version = current.Version + 1, Allowed = allowed };
        }
        // Policy enforcement never depends on a UI notification succeeding.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { }
    }
    public void EnsureCurrent(ModelModificationSnapshot snapshot)
    { if (Snapshot != snapshot) throw new ModelModificationDeniedException(); }
    public ModelModificationSnapshot RequireAllowed()
    { var snapshot = Snapshot; if (!snapshot.Allowed) throw new ModelModificationDeniedException(); return snapshot; }
    public void EnsureAllowed(ModelModificationSnapshot snapshot)
    { EnsureCurrent(snapshot); if (!snapshot.Allowed) throw new ModelModificationDeniedException(); }
    public IModelDefinitionWriter Guard(IModelDefinitionWriter writer) => writer is PolicyControlledModelWriter guarded && ReferenceEquals(guarded.Policy, this)
        ? writer : new PolicyControlledModelWriter(writer, this);
}

// Authoritative forward-write boundary; reads remain available in safe mode.
// Compensation is limited to plans which this wrapper actually started.
public sealed class PolicyControlledModelWriter(IModelDefinitionWriter inner, ModelModificationPolicy policy) : IModelDefinitionWriter
{
    private readonly ConcurrentDictionary<Guid, string> started = new();
    public ModelModificationPolicy Policy => policy;
    public Task<DefinitionState> ReadDefinitionAsync(CancellationToken token) => inner.ReadDefinitionAsync(token);
    public async Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
    {
        var epoch = policy.RequireAllowed();
        void Guard() { policy.EnsureAllowed(epoch); ensureAuthorized(); token.ThrowIfCancellationRequested(); }
        Guard();
        if (started.Count >= 256) throw new InvalidOperationException("政策工作階段操作已達上限。");
        var hash = DefinitionRules.HashPlan(plan);
        if (!started.TryAdd(plan.OperationId, hash) && started[plan.OperationId] != hash) throw new DefinitionConflictException();
        await inner.ApplyAsync(plan, Guard, token);
        Guard();
    }
    public Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token)
    {
        if (!started.TryGetValue(preview.Plan.OperationId, out var hash) || hash != DefinitionRules.HashPlan(preview.Plan))
            throw new ModelModificationDeniedException();
        DefinitionRules.ValidateRollback(preview, observed);
        return inner.RestoreAsync(preview, observed, token);
    }
}
