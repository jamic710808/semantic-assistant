using System.Text.Json.Serialization;
using Microsoft.Identity.Client;

namespace SemanticAssistant.Adapters.PowerBI.Cloud;

// Avoid record-generated ToString and JSON serialization of credentials.
public sealed class DelegatedToken(string value, DateTimeOffset expiresAt, Guid tenant)
{
    [JsonIgnore] public string Value { get; } = value;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public Guid Tenant { get; } = tenant;
    public override string ToString() => "DelegatedToken(redacted)";
}
public interface IDelegatedTokenSource { Task<DelegatedToken> AcquireAsync(CancellationToken token); }
public interface IModelWriteTokenSource { Task<DelegatedToken> AcquireWriteAsync(bool allowInteractive, CancellationToken token); }

public sealed class MsalUserIdentity : IDelegatedTokenSource, IModelWriteTokenSource, IDisposable
{
    private readonly IPublicClientApplication application;
    private IAccount? account;
    private readonly Guid tenant;
    private readonly SemaphoreSlim operations = new(1, 1);
    private MsalCachePersistence? persistence;
    private volatile bool disposed;
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/Dataset.Read.All",
        "https://analysis.windows.net/powerbi/api/Workspace.Read.All"];
    private static readonly string[] WriteScopes = ["https://analysis.windows.net/powerbi/api/Dataset.ReadWrite.All",
        "https://analysis.windows.net/powerbi/api/Workspace.Read.All"];

    public MsalUserIdentity(Guid clientId, Guid tenantId, bool rememberLogin = false, string? cacheDirectory = null)
    {
        if (clientId == Guid.Empty || tenantId == Guid.Empty) throw new ArgumentException("需要有效的 public-client application 與 tenant ID。");
        tenant = tenantId;
        application = PublicClientApplicationBuilder.Create(clientId.ToString()).WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithRedirectUri("http://localhost").Build();
        // The default remains memory only. No cache directory is opened without explicit opt-in.
        if (rememberLogin)
        {
            var store = new CloudLoginCache(clientId, tenantId, cacheDirectory);
            try { persistence = new(application.UserTokenCache, store); }
            catch { store.Dispose(); throw; }
        }
    }
    public Task SignInAsync(CancellationToken token, bool selectDifferentAccount = false) => RunAsync(async () =>
    {
        var pin = selectDifferentAccount ? null : persistence?.PinnedAccountId;
        IAccount? saved = null;
        if (pin is not null)
        {
            var accounts = (await application.GetAccountsAsync()).ToArray();
            if (accounts.Length != 1 || accounts[0].HomeAccountId.Identifier != pin) throw new CloudLoginCacheException();
            saved = accounts[0];
            try
            {
                var cached = await application.AcquireTokenSilent(Scopes, saved).ExecuteAsync(token);
                Validate(cached, pin); EnsureActive(token);
                persistence!.CommitValidatedAccount(pin, newInteractiveLogin: false);
                account = cached.Account; return true;
            }
            catch (MsalUiRequiredException) { /* Browser reauthentication for the SAME pinned account. */ }
        }
        var builder = application.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false);
        if (saved is not null) builder = builder.WithAccount(saved);
        var result = await builder.ExecuteAsync(token);
        Validate(result, pin); EnsureActive(token);
        // Switching requires the explicit UI choice. Do not persist another account's tokens.
        if (persistence is not null)
        {
            foreach (var other in await application.GetAccountsAsync())
                if (other.HomeAccountId.Identifier != result.Account.HomeAccountId.Identifier) await application.RemoveAsync(other);
            EnsureActive(token);
            persistence.CommitValidatedAccount(result.Account.HomeAccountId.Identifier, newInteractiveLogin: true);
        }
        account = result.Account; return true;
    }, token);
    public Task<DelegatedToken> AcquireAsync(CancellationToken token) => RunAsync(async () =>
    {
        var selected = account ?? throw new InvalidOperationException("請先由使用者登入。");
        var result = await application.AcquireTokenSilent(Scopes, selected).ExecuteAsync(token);
        Validate(result, selected.HomeAccountId.Identifier); EnsureActive(token);
        persistence?.CommitValidatedAccount(selected.HomeAccountId.Identifier, newInteractiveLogin: false);
        return new DelegatedToken(result.AccessToken, result.ExpiresOn, tenant);
    }, token);
    private void Validate(AuthenticationResult result, string? expectedAccount) =>
        CloudAccountBinding.Validate(tenant, result.TenantId, result.Account?.HomeAccountId.Identifier, result.ExpiresOn, expectedAccount);
    public Task<DelegatedToken> AcquireWriteAsync(bool allowInteractive, CancellationToken token) => RunAsync(async () =>
    {
        var pinnedAccount = account ?? throw new InvalidOperationException("請先由使用者登入。");
        AuthenticationResult result;
        try { result = await application.AcquireTokenSilent(WriteScopes, pinnedAccount).ExecuteAsync(token); }
        catch (MsalUiRequiredException) when (allowInteractive)
        {
            result = await application.AcquireTokenInteractive(WriteScopes).WithAccount(pinnedAccount)
                .WithUseEmbeddedWebView(false).ExecuteAsync(token);
        }
        Validate(result, pinnedAccount.HomeAccountId.Identifier); EnsureActive(token);
        if (account?.HomeAccountId.Identifier != pinnedAccount.HomeAccountId.Identifier ||
            result.Account.HomeAccountId.Identifier != pinnedAccount.HomeAccountId.Identifier)
            throw new InvalidOperationException("寫入帳號與原先選取帳號不符。");
        persistence?.CommitValidatedAccount(pinnedAccount.HomeAccountId.Identifier, newInteractiveLogin: false);
        return new DelegatedToken(result.AccessToken, result.ExpiresOn, tenant);
    }, token);
    public Task SignOutAsync() => RunAsync(async () =>
    {
        account = null;
        try { foreach (var existing in await application.GetAccountsAsync()) await application.RemoveAsync(existing); }
        finally { persistence?.Clear(); }
        return true;
    }, CancellationToken.None);
    private void EnsureActive(CancellationToken token) { token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this); }
    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await operations.WaitAsync(token);
        try { EnsureActive(token); return await action(); }
        finally { if (disposed) ReleaseCache(); operations.Release(); }
    }
    private void ReleaseCache() { persistence?.Dispose(); persistence = null; account = null; }
    public void Dispose()
    {
        disposed = true;
        // An in-flight MSAL operation releases its resources in RunAsync's finally.
        if (operations.Wait(0)) { try { ReleaseCache(); } finally { operations.Release(); } }
    }
}
