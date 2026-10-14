namespace SemanticAssistant.Application;

// Only fixed categories cross the adapter boundary; remote text and secrets do not.
public enum AiProviderFailure
{
    InvalidRequest, Authentication, AccessDenied, NotFound, RateLimited,
    ServiceUnavailable, UnexpectedHttpStatus, Network, Protocol
}

public class AiProviderFailureException(AiProviderFailure failure)
    : Exception("AI 供應商請求失敗。")
{
    public AiProviderFailure Failure { get; } = failure;
}
