using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using SemanticAssistant.Adapters.PowerBI.Cloud;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class CloudLoginCacheTests
{
    private static readonly Guid Client = new("03cc925a-40b2-4f46-9828-92dfd39346a7");
    private static readonly Guid Tenant = new("7053a013-98de-4252-9737-e776689880d0");
    private static string Root() => Path.Combine(Path.GetTempPath(), "SemanticAssistant-cache-test-" + Guid.NewGuid().ToString("N"));
    private static byte[] Bytes(string value = "synthetic-access-and-refresh-bytes") => Encoding.UTF8.GetBytes(value);
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task DefaultIdentityDoesNotCreateOrReadAnyPersistentCache()
    {
        var root = Root();
        using var identity = new MsalUserIdentity(Client, Tenant, cacheDirectory: root);
        Assert.False(Directory.Exists(root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.AcquireAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.AcquireWriteAsync(false, default));
        await identity.SignOutAsync(); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task DisposedIdentityCannotRestoreLoginOrAcquireTokens()
    {
        var identity = new MsalUserIdentity(Client, Tenant); identity.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => identity.AcquireAsync(default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => identity.SignInAsync(default));
    }
    [Fact]
    public void RoundTripUsesActualWindowsProtectionAndDoesNotWritePlaintext()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Throws<PlatformNotSupportedException>(() => new CloudLoginCache(Client, Tenant)); return; }
        var root = Root(); string file;
        using (var cache = new CloudLoginCache(Client, Tenant, root))
        {
            cache.Save("synthetic-home-account", DateTimeOffset.UtcNow.AddDays(1), Bytes()); file = cache.CachePath;
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("synthetic-home-account", text); Assert.DoesNotContain("synthetic-access-and-refresh-bytes", text);
        }
        using var reopened = new CloudLoginCache(Client, Tenant, root);
        using var entry = reopened.Read(); Assert.NotNull(entry);
        Assert.Equal("synthetic-home-account", entry.AccountId); Assert.Equal(Bytes(), entry.MsalBytes);
        Assert.DoesNotContain("synthetic", entry.ToString()); Assert.DoesNotContain("synthetic", JsonSerializer.Serialize(entry));
        var shared = entry.MsalBytes; entry.Dispose(); Assert.All(shared, b => Assert.Equal(0, b));
        reopened.Clear(); Assert.False(File.Exists(file)); Assert.Null(reopened.Read());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopiedCiphertextCannotBeUsedByAnotherClientOrTenant(bool changeClient)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root(); byte[] cipher;
        using (var first = new CloudLoginCache(Client, Tenant, root))
        { first.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), Bytes()); cipher = File.ReadAllBytes(first.CachePath); }
        using var other = new CloudLoginCache(changeClient ? Guid.NewGuid() : Client, changeClient ? Tenant : Guid.NewGuid(), root);
        File.WriteAllBytes(other.CachePath, cipher);
        var error = Assert.Throws<CloudLoginCacheException>(() => other.Read()); Assert.Null(error.InnerException);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CorruptionOversizeAndEmptyFilesFailClosedWithoutLeakingTokens(int kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cache = new CloudLoginCache(Client, Tenant, Root());
        cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), Bytes());
        var content = File.ReadAllBytes(cache.CachePath);
        if (kind == 0) content[^1] ^= 0x3f;
        else content = kind == 1 ? new byte[CloudLoginCache.MaximumFileBytes + 1] : [];
        File.WriteAllBytes(cache.CachePath, content);
        var error = Assert.Throws<CloudLoginCacheException>(() => cache.Read()); Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-account", error.ToString());
        cache.Clear(); Assert.Null(cache.Read());
    }
    [Fact]
    public void ExclusiveLeasePreventsConcurrentSaveOrForgetButReleasesOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root();
        using (var cache = new CloudLoginCache(Client, Tenant, root))
        {
            cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), Bytes());
            Assert.Throws<CloudLoginCacheException>(() => new CloudLoginCache(Client, Tenant, root));
            Assert.Throws<CloudLoginCacheException>(() => CloudLoginCache.Forget(Client, Tenant, root));
            Assert.NotNull(cache.Read());
        }
        CloudLoginCache.Forget(Client, Tenant, root);
        using var reopened = new CloudLoginCache(Client, Tenant, root); Assert.Null(reopened.Read());
    }
    [Fact]
    public void ExpiryCannotBeRenewedByReadingAndClockRollbackFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clock = new Clock(); using var cache = new CloudLoginCache(Client, Tenant, Root(), clock);
        cache.Save("synthetic-account", clock.Now.AddDays(30), Bytes());
        clock.Now = clock.Now.AddDays(-1); Assert.Throws<CloudLoginCacheException>(() => cache.Read());
        clock.Now = clock.Now.AddDays(32); Assert.Null(cache.Read()); Assert.True(File.Exists(cache.CachePath));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(512001)]
    public void InvalidCacheSizeDoesNotOverwriteExistingCiphertext(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cache = new CloudLoginCache(Client, Tenant, Root());
        cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), Bytes()); var original = File.ReadAllBytes(cache.CachePath);
        Assert.Throws<CloudLoginCacheException>(() => cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), new byte[length]));
        Assert.Equal(original, File.ReadAllBytes(cache.CachePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cache.CachePath)!, "*.tmp-*"));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void InvalidRetentionIsRefused(int days)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cache = new CloudLoginCache(Client, Tenant, Root());
        Assert.Throws<CloudLoginCacheException>(() => cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(days), Bytes()));
        Assert.False(File.Exists(cache.CachePath));
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("account\n")]
    public void InvalidAccountNamesAreRefused(string account)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cache = new CloudLoginCache(Client, Tenant, Root());
        Assert.Throws<CloudLoginCacheException>(() => cache.Save(account, DateTimeOffset.UtcNow.AddDays(1), Bytes()));
    }
    [Fact]
    public void MaximumCacheAndAtomicReplacementRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cache = new CloudLoginCache(Client, Tenant, Root());
        var large = RandomNumberGenerator.GetBytes(CloudLoginCache.MaximumMsalBytes);
        cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), large);
        using (var entry = cache.Read()) Assert.Equal(large, entry!.MsalBytes);
        cache.Save("synthetic-new-account", DateTimeOffset.UtcNow.AddDays(1), Bytes("synthetic-replacement"));
        using var replaced = cache.Read(); Assert.Equal("synthetic-new-account", replaced!.AccountId);
        Assert.Equal(Bytes("synthetic-replacement"), replaced.MsalBytes);
    }
    [Fact]
    public void CacheDirectoryOrTargetDirectoryCannotBeUsedAsAFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root(); File.WriteAllText(root, "synthetic-marker");
        Assert.Throws<CloudLoginCacheException>(() => new CloudLoginCache(Client, Tenant, root));
        using var cache = new CloudLoginCache(Client, Tenant, Root()); Directory.CreateDirectory(cache.CachePath);
        Assert.Throws<CloudLoginCacheException>(() => cache.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), Bytes()));
        Assert.Throws<CloudLoginCacheException>(() => cache.Clear()); Assert.True(Directory.Exists(cache.CachePath));
    }
    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-account")]
    [InlineData("expired")]
    [InlineData("missing-account")]
    public void AuthenticationResultsMustMatchThePinnedAccountTenantAndExpiry(string reason)
    {
        var actualTenant = reason == "wrong-tenant" ? Guid.NewGuid().ToString() : Tenant.ToString();
        var account = reason == "wrong-account" ? "another" : reason == "missing-account" ? "" : "synthetic-account";
        var expires = DateTimeOffset.UtcNow.AddMinutes(reason == "expired" ? -1 : 5);
        Assert.Throws<InvalidOperationException>(() => CloudAccountBinding.Validate(Tenant, actualTenant, account, expires, "synthetic-account"));
    }
    [Fact]
    public void AccountBindingAcceptsExactMatchAndRefusesEmptyTenant()
    {
        CloudAccountBinding.Validate(Tenant, Tenant.ToString(), "synthetic-account", DateTimeOffset.UtcNow.AddMinutes(5), "synthetic-account");
        Assert.Throws<InvalidOperationException>(() => CloudAccountBinding.Validate(Guid.Empty, Guid.Empty.ToString(), "synthetic-account", DateTimeOffset.UtcNow.AddMinutes(5)));
    }
    [Fact]
    public async Task RestartRefusesAStoredPinWhoseAccountIsNotInTheMsalCache()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root();
        var app = PublicClientApplicationBuilder.Create(Client.ToString()).WithAuthority("https://login.microsoftonline.com/" + Tenant).Build();
        using (var persistence = new MsalCachePersistence(app.UserTokenCache, new(Client, Tenant, root)))
        { await app.GetAccountsAsync(); persistence.CommitValidatedAccount("synthetic-nonexistent-account", true); }
        using var identity = new MsalUserIdentity(Client, Tenant, true, root);
        // Fails on local account enumeration, before silent token acquisition or a browser.
        await Assert.ThrowsAsync<CloudLoginCacheException>(() => identity.SignInAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.AcquireAsync(default));
        await identity.SignOutAsync();
    }
    [Fact]
    public async Task InvalidSerializedMsalBytesAreNotImportedAndDoNotBecomeAnEmptyCache()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root();
        using (var store = new CloudLoginCache(Client, Tenant, root)) store.Save("synthetic-account", DateTimeOffset.UtcNow.AddDays(1), Bytes("not-msal-json"));
        var app = PublicClientApplicationBuilder.Create(Client.ToString()).Build();
        using var persistence = new MsalCachePersistence(app.UserTokenCache, new(Client, Tenant, root));
        await Assert.ThrowsAsync<CloudLoginCacheException>(() => app.GetAccountsAsync());
        persistence.Clear();
    }
    [Fact]
    public void ConstructorFailureReleasesItsLeaseSoTheUserCanForgetTheDamagedProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root();
        using (var store = new CloudLoginCache(Client, Tenant, root)) File.WriteAllBytes(store.CachePath, Bytes("corrupt-ciphertext"));
        Assert.Throws<CloudLoginCacheException>(() => new MsalUserIdentity(Client, Tenant, true, root));
        CloudLoginCache.Forget(Client, Tenant, root);
    }
    [Fact]
    public async Task CancelledAcquisitionDoesNotLeakTheOperationSemaphore()
    {
        using var identity = new MsalUserIdentity(Client, Tenant); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => identity.AcquireAsync(cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.AcquireAsync(default));
        await identity.SignOutAsync();
    }
    [Fact]
    public async Task SilentCacheCommitPreservesTheOriginalRetentionDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root();
        var first = PublicClientApplicationBuilder.Create(Client.ToString()).Build(); DateTimeOffset deadline;
        using (var persistence = new MsalCachePersistence(first.UserTokenCache, new(Client, Tenant, root)))
        { await first.GetAccountsAsync(); persistence.CommitValidatedAccount("synthetic-account", true); }
        using (var store = new CloudLoginCache(Client, Tenant, root)) using (var entry = store.Read()) deadline = entry!.ExpiresAt;
        var second = PublicClientApplicationBuilder.Create(Client.ToString()).Build();
        using (var persistence = new MsalCachePersistence(second.UserTokenCache, new(Client, Tenant, root)))
        { await second.GetAccountsAsync(); persistence.CommitValidatedAccount("synthetic-account", false); }
        using var reopened = new CloudLoginCache(Client, Tenant, root); using var result = reopened.Read(); Assert.Equal(deadline, result!.ExpiresAt);
    }
    [Fact]
    public void JunctionDirectoriesAreRejectedBeforeReadingOrSavingAnyCache()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root(); Directory.CreateDirectory(root); var actual = Path.Combine(root, "actual"); Directory.CreateDirectory(actual);
        var redirect = Path.Combine(root, "redirect");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", redirect, actual }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
        Assert.Throws<CloudLoginCacheException>(() => new CloudLoginCache(Client, Tenant, redirect));
        Assert.Empty(Directory.GetFiles(actual));
    }
    [Fact]
    public async Task RealMsalNotificationsDoNotPersistUntilAnExplicitValidatedCommit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Root(); var app = PublicClientApplicationBuilder.Create(Client.ToString()).WithAuthority("https://login.microsoftonline.com/" + Tenant).Build();
        var store = new CloudLoginCache(Client, Tenant, root); var file = store.CachePath;
        using (var persistence = new MsalCachePersistence(app.UserTokenCache, store))
        {
            Assert.Empty(await app.GetAccountsAsync()); Assert.False(File.Exists(file));
            Assert.Throws<CloudLoginCacheException>(() => persistence.CommitValidatedAccount("synthetic-account", false));
            // Synthetic empty MSAL cache only; no authentication or external HTTP is performed.
            persistence.CommitValidatedAccount("synthetic-account", true); Assert.True(File.Exists(file));
        }
        var next = PublicClientApplicationBuilder.Create(Client.ToString()).WithAuthority("https://login.microsoftonline.com/" + Tenant).Build();
        using var reopened = new MsalCachePersistence(next.UserTokenCache, new(Client, Tenant, root));
        Assert.Equal("synthetic-account", reopened.PinnedAccountId); Assert.Empty(await next.GetAccountsAsync());
        reopened.Clear(); Assert.False(File.Exists(file)); Assert.Null(reopened.PinnedAccountId);
    }
}
