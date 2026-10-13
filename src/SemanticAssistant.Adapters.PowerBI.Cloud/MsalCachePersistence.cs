using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace SemanticAssistant.Adapters.PowerBI.Cloud;

// MSAL serializes inside its cache notification. Disk commit happens only AFTER
// the caller validates the authentication result and explicitly pins an account.
public sealed class MsalCachePersistence : IDisposable
{
    private readonly CloudLoginCache store;
    private readonly ITokenCache cache;
    private CloudLoginCacheEntry? incoming;
    private byte[]? pending;
    public string? PinnedAccountId { get; private set; }
    private DateTimeOffset retentionDeadline;

    public MsalCachePersistence(ITokenCache cache, CloudLoginCache store)
    {
        this.cache = cache; this.store = store;
        incoming = store.Read();
        PinnedAccountId = incoming?.AccountId; retentionDeadline = incoming?.ExpiresAt ?? default;
        cache.SetBeforeAccess(BeforeAccess); cache.SetAfterAccess(AfterAccess);
    }
    private void BeforeAccess(TokenCacheNotificationArgs args)
    {
        if (incoming is null) return;
        try { args.TokenCache.DeserializeMsalV3(incoming.MsalBytes, shouldClearExistingCache: true); }
        catch { throw new CloudLoginCacheException(); }
        finally { incoming.Dispose(); incoming = null; }
    }
    private void AfterAccess(TokenCacheNotificationArgs args)
    {
        ClearPending();
        var bytes = args.TokenCache.SerializeMsalV3();
        if (bytes.Length > CloudLoginCache.MaximumMsalBytes)
        { CryptographicOperations.ZeroMemory(bytes); throw new CloudLoginCacheException(); }
        pending = bytes;
    }
    public void CommitValidatedAccount(string accountId, bool newInteractiveLogin)
    {
        if (pending is null || !newInteractiveLogin && PinnedAccountId != accountId) throw new CloudLoginCacheException();
        var deadline = newInteractiveLogin ? DateTimeOffset.UtcNow + CloudLoginCache.MaximumRetention : retentionDeadline;
        try { store.Save(accountId, deadline, pending); PinnedAccountId = accountId; retentionDeadline = deadline; }
        finally { ClearPending(); }
    }
    public void Clear()
    {
        incoming?.Dispose(); incoming = null; ClearPending(); PinnedAccountId = null;
        store.Clear();
    }
    private void ClearPending() { if (pending is not null) CryptographicOperations.ZeroMemory(pending); pending = null; }
    public void Dispose()
    {
        cache.SetBeforeAccess((TokenCacheCallback?)null); cache.SetAfterAccess((TokenCacheCallback?)null);
        incoming?.Dispose(); incoming = null; ClearPending(); store.Dispose();
    }
}
