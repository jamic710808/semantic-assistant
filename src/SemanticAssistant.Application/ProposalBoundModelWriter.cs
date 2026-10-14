namespace SemanticAssistant.Application;

// Freshness/mode checks reach the native adapter's final pre-save callback.
// Compensation deliberately remains available after proposal authority expires.
public sealed class ProposalBoundModelWriter(IModelDefinitionWriter inner, Action guard) : IModelDefinitionWriter
{
    public Task<DefinitionState> ReadDefinitionAsync(CancellationToken token) => inner.ReadDefinitionAsync(token);
    public async Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
    {
        guard();
        await inner.ApplyAsync(plan, () => { guard(); ensureAuthorized(); token.ThrowIfCancellationRequested(); }, token);
        guard();
    }
    public Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token) => inner.RestoreAsync(preview, observed, token);
}
