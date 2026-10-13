using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public enum ModelSourceKind { Synthetic, OfflineTmdl, Desktop, Cloud }
public sealed record WorkspaceSelection(ModelSnapshot Model, ModelSourceKind Source)
{
    public bool IsOffline => Source == ModelSourceKind.OfflineTmdl;
}

// Commit a new selection only after a complete read; stale or cancelled reads never replace it.
public sealed class ModelWorkspace(ModelSnapshot initialDemo)
{
    private readonly object sync = new();
    private WorkspaceSelection selection = new(initialDemo, ModelSourceKind.Synthetic);
    private long generation;

    public WorkspaceSelection Current { get { lock (sync) return selection; } }

    public void ResetToDemo()
    {
        lock (sync)
        {
            generation++;
            selection = new(initialDemo, ModelSourceKind.Synthetic);
        }
    }

    public Task<bool> LoadOfflineAsync(ISemanticModelReader reader, CancellationToken cancellationToken) =>
        LoadAsync(reader, ModelSourceKind.OfflineTmdl, cancellationToken);

    public async Task<bool> LoadAsync(ISemanticModelReader reader, ModelSourceKind source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long loadingGeneration;
        lock (sync) loadingGeneration = ++generation;
        var model = await reader.ReadSnapshotAsync(cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loadingGeneration != generation) return false;
            selection = new(model, source);
            return true;
        }
    }
}
