using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record AssistantBudgets(
    [property: JsonRequired] int ContextTables = 20,
    [property: JsonRequired] int ContextCharacters = 2_000,
    [property: JsonRequired] int QueryRows = 100,
    [property: JsonRequired] int QueryBytes = 64_000,
    [property: JsonRequired] int QueryTimeoutSeconds = 30,
    [property: JsonRequired] int TurnRounds = 6,
    [property: JsonRequired] int TurnToolCalls = 8,
    [property: JsonRequired] int TurnCharacters = 20_000,
    [property: JsonRequired] int TurnTimeoutSeconds = 120)
{
    public void Validate()
    {
        new ContextBudget(ContextTables, ContextCharacters).Validate();
        if (QueryRows is < 1 or > 100 || QueryBytes is < 1_024 or > 64_000 || QueryTimeoutSeconds is < 1 or > 30 ||
            TurnRounds is < 1 or > 6 || TurnToolCalls is < 1 or > 8 || TurnCharacters is < 128 or > 20_000 ||
            TurnTimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("設定預算超出允許範圍。");
    }

    public QueryLimits QueryLimits(bool cloud) => new(QueryRows, QueryBytes,
        TimeSpan.FromSeconds(Math.Min(QueryTimeoutSeconds, cloud ? 30 : 10)));
    public TurnLimits TurnLimits() => new(TurnRounds, TurnToolCalls, TurnCharacters, TimeSpan.FromSeconds(TurnTimeoutSeconds));
}

public sealed record AssistantSettings([property: JsonRequired] int Version,
    [property: JsonRequired] ProviderIdentity? AiProvider, [property: JsonRequired] AssistantBudgets Budgets)
{
    public static AssistantSettings Default => new(2, null, new());
    public void Validate()
    {
        if (Version != 2 || Budgets is null) throw new InvalidDataException("設定版本或預算無效。");
        Budgets.Validate();
        if (AiProvider is { } profile && ProviderProfiles.Validate(profile) != profile)
            throw new InvalidDataException("請保存正規化的供應商端點。");
    }
}

public static class ProviderProfiles
{
    public static ProviderIdentity Validate(ProviderIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Id) || identity.Id.Length > 128 || identity.Id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(identity.Model) || identity.Model.Length > 128 || identity.Model.Any(char.IsControl) ||
            identity.Endpoint is null || identity.Endpoint.Length > 2_048 ||
            !Uri.TryCreate(identity.Endpoint, UriKind.Absolute, out var uri) ||
            uri.AbsoluteUri.Length > 2_048 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            throw new ArgumentException("端點必須是 HTTPS 的完整 /chat/completions URL；本機服務允許 loopback HTTP。");
        return identity with { Endpoint = uri.AbsoluteUri };
    }
}
