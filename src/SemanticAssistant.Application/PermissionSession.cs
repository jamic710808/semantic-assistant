using System.Collections.Immutable;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// Revocation or a target/mode change invalidates previously issued snapshots.
public sealed class PermissionSession
{
    private readonly object sync = new();
    private PermissionSnapshot current;

    public PermissionSession(ModelIdentity target, ConversationMode mode, ImmutableHashSet<Capability> granted,
        EgressConsent? egress = null)
        => current = new(Guid.NewGuid(), 1, target, mode, granted) { Egress = egress };

    public PermissionSnapshot Snapshot { get { lock (sync) return current; } }

    public void Update(ModelIdentity target, ConversationMode mode, ImmutableHashSet<Capability> granted,
        EgressConsent? egress = null)
    {
        lock (sync) current = current with { Version = current.Version + 1, Target = target, Mode = mode, Granted = granted, Egress = egress };
    }

    public bool IsCurrent(PermissionSnapshot snapshot)
    {
        lock (sync) return snapshot == current;
    }
}
