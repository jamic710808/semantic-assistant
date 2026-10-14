using System.Collections.Immutable;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// Pressing Send is the user's authorization to use the active provider for this turn.
// The PermissionSession still pins the provider, full model identity, capabilities and cancellation epoch.
public static class AiTurnConsent
{
    public static EgressConsent ForSend(ProviderIdentity provider)
    {
        var categories = ImmutableHashSet.Create(EgressCategory.UserMessage, EgressCategory.Metadata,
            EgressCategory.QueryResults, EgressCategory.ExternalToolData);
        return new(provider, categories, DateTimeOffset.UtcNow.AddMinutes(10));
    }
}
