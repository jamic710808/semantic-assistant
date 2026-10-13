using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed class EgressAuthorizationException() : Exception("外送授權不存在、過期或已撤銷。");

// Only the application can issue this lease. It is tied to the complete permission snapshot.
public sealed class ProviderAuthorization
{
    private readonly PermissionSession session;
    private readonly PermissionSnapshot snapshot;
    internal ProviderAuthorization(PermissionSession session, PermissionSnapshot snapshot)
    { this.session = session; this.snapshot = snapshot; }

    public bool Allows(EgressCategory category) => snapshot.Egress?.Categories.Contains(category) == true;

    public void EnsureValid(ProviderIdentity identity)
    {
        if (!session.IsCurrent(snapshot) || snapshot.Egress is not { } consent ||
            consent.Provider != identity || consent.ExpiresAt <= DateTimeOffset.UtcNow ||
            !Allows(EgressCategory.UserMessage))
            throw new EgressAuthorizationException();
    }
}
