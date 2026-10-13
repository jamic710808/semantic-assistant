using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SemanticAssistant.Application;

namespace SemanticAssistant.Adapters.PowerBI.Cloud;

public sealed class CloudLoginCacheException : Exception
{
    public CloudLoginCacheException() : base("登入快取不可用；請先登出或清除本程式快取，再重新登入。") { }
}

public sealed class CloudLoginCacheEntry(string accountId, DateTimeOffset expiresAt, byte[] msalBytes) : IDisposable
{
    [JsonIgnore] public string AccountId { get; } = accountId;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    [JsonIgnore] public byte[] MsalBytes { get; } = msalBytes;
    public override string ToString() => "CloudLoginCacheEntry(redacted)";
    public void Dispose() => CryptographicOperations.ZeroMemory(MsalBytes);
}

// One explicitly enabled profile per Windows user, public-client application and tenant.
// An exclusive file lease prevents independent instances from overwriting a refreshed cache.
public sealed class CloudLoginCache : IDisposable
{
    public const int MaximumMsalBytes = 512_000;
    public const int MaximumFileBytes = 1_048_576;
    public static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(30);
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SemanticAssistant", "cloud-login");
    private readonly Guid client;
    private readonly Guid tenant;
    private readonly TimeProvider clock;
    private readonly string root;
    private readonly byte[] entropy;
    private FileStream? lease;
    public string CachePath { get; }
    private sealed record Envelope(int Version, Guid ClientId, Guid TenantId, string AccountId,
        DateTimeOffset ExpiresAt, byte[] MsalBytes);

    public CloudLoginCache(Guid clientId, Guid tenantId, string? directory = null, TimeProvider? timeProvider = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("登入快取需要 Windows DPAPI。");
        if (clientId == Guid.Empty || tenantId == Guid.Empty) throw new ArgumentException("需要有效的 application 與 tenant ID。");
        client = clientId; tenant = tenantId; clock = timeProvider ?? TimeProvider.System;
        root = Path.GetFullPath(directory ?? DefaultRoot);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length > 200) throw new CloudLoginCacheException();
        entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"SemanticAssistant.CloudLogin.v1|{client:D}|{tenant:D}"));
        CachePath = Path.Combine(root, Convert.ToHexString(entropy).ToLowerInvariant() + ".bin");
        try
        {
            CheckParents(); Directory.CreateDirectory(root); CheckParents();
            var lockPath = CachePath + ".lock"; CheckFile(lockPath);
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CheckFile(CachePath);
        }
        catch { lease?.Dispose(); lease = null; throw new CloudLoginCacheException(); }
    }

    public CloudLoginCacheEntry? Read()
    {
        EnsureOpen();
        byte[]? plain = null;
        byte[]? decoded = null;
        try
        {
            CheckParents(); CheckFile(CachePath);
            if (!File.Exists(CachePath)) return null;
            using var input = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > MaximumFileBytes) throw new CloudLoginCacheException();
            var cipher = new byte[checked((int)input.Length)]; input.ReadExactly(cipher);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            plain = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.CurrentUser);
            if (plain.Length is < 1 or > MaximumFileBytes) throw new CloudLoginCacheException();
            var entry = StrictJson.Parse<Envelope>(new UTF8Encoding(false, true).GetString(plain), MaximumFileBytes);
            decoded = entry.MsalBytes;
            if (entry.Version != 1 || entry.ClientId != client || entry.TenantId != tenant ||
                !ValidAccount(entry.AccountId) || entry.MsalBytes is null || entry.MsalBytes.Length is < 1 or > MaximumMsalBytes ||
                entry.ExpiresAt > clock.GetUtcNow() + MaximumRetention) throw new CloudLoginCacheException();
            if (entry.ExpiresAt <= clock.GetUtcNow()) return null;
            var result = new CloudLoginCacheEntry(entry.AccountId, entry.ExpiresAt, entry.MsalBytes);
            decoded = null; return result;
        }
        catch { throw new CloudLoginCacheException(); }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public void Save(string accountId, DateTimeOffset expiresAt, byte[] msalBytes)
    {
        EnsureOpen();
        if (!ValidAccount(accountId) || msalBytes.Length is < 1 or > MaximumMsalBytes ||
            expiresAt <= clock.GetUtcNow() || expiresAt > clock.GetUtcNow() + MaximumRetention)
            throw new CloudLoginCacheException();
        byte[]? plain = null;
        string? temporary = null;
        try
        {
            CheckParents(); CheckFile(CachePath);
            plain = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, client, tenant, accountId, expiresAt, msalBytes));
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var cipher = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
            if (cipher.Length > MaximumFileBytes) throw new CloudLoginCacheException();
            temporary = CachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(cipher); output.Flush(flushToDisk: true); }
            CheckParents(); CheckFile(CachePath); CheckFile(temporary);
            File.Move(temporary, CachePath, overwrite: true); temporary = null;
        }
        catch { throw new CloudLoginCacheException(); }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (temporary is not null)
            { try { CheckParents(); CheckFile(temporary); File.Delete(temporary); } catch { } }
        }
    }

    public void Clear()
    {
        EnsureOpen();
        try { CheckParents(); CheckFile(CachePath); File.Delete(CachePath); }
        catch { throw new CloudLoginCacheException(); }
    }
    public static void Forget(Guid clientId, Guid tenantId, string? directory = null)
    { using var cache = new CloudLoginCache(clientId, tenantId, directory); cache.Clear(); }
    private void EnsureOpen() { if (lease is null) throw new ObjectDisposedException(nameof(CloudLoginCache)); }
    private static bool ValidAccount(string? account) => !string.IsNullOrWhiteSpace(account) && account.Length <= 512 && !account.Any(char.IsControl);
    private void CheckParents()
    {
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new CloudLoginCacheException();
    }
    private static void CheckFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new CloudLoginCacheException();
    }
    public void Dispose() { lease?.Dispose(); lease = null; }
}

public static class CloudAccountBinding
{
    public static void Validate(Guid expectedTenant, string? actualTenant, string? actualAccount,
        DateTimeOffset expiresAt, string? expectedAccount = null, TimeProvider? clock = null)
    {
        if (expectedTenant == Guid.Empty || !Guid.TryParse(actualTenant, out var tenant) || tenant != expectedTenant ||
            string.IsNullOrWhiteSpace(actualAccount) || actualAccount.Length > 512 || actualAccount.Any(char.IsControl) ||
            expiresAt <= (clock ?? TimeProvider.System).GetUtcNow() ||
            expectedAccount is not null && !string.Equals(expectedAccount, actualAccount, StringComparison.Ordinal))
            throw new InvalidOperationException("登入帳號、租用戶或有效期限不符。");
    }
}
