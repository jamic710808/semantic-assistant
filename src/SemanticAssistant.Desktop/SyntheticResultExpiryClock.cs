namespace SemanticAssistant.Desktop;

// Only synthetic smoke paths supply this host clock; it never changes the OS clock.
internal sealed class SyntheticResultExpiryClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddMinutes(6);
}
