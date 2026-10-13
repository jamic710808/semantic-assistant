using System.Text;
using System.Text.Json;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class SettingsStoreTests
{
    private static readonly ProviderIdentity Profile = new("fixture", "https://example.invalid/v1/chat/completions", "synthetic");

    [Fact]
    public void MissingSettingsDoesNotCreateFilesOrRestorePermissions()
    {
        using var fixture = new Fixture(); var loaded = fixture.Store.Load();
        Assert.Equal(SettingsLoadStatus.Missing, loaded.Status);
        Assert.Null(loaded.Settings.AiProvider); Assert.Equal(AssistantSettings.Default, loaded.Settings);
        Assert.False(Directory.Exists(fixture.Folder));
        Assert.DoesNotContain("Egress", JsonSerializer.Serialize(loaded.Settings));
    }

    [Fact]
    public void IndependentStoreRestartRoundTripsNonSecretSettingsAndBudgets()
    {
        using var fixture = new Fixture(); var settings = AssistantSettings.Default with { AiProvider = Profile, Budgets = new(QueryRows: 2, ContextTables: 1) };
        fixture.Store.Save(settings);
        var loaded = new FileAssistantSettingsStore(fixture.Folder).Load();
        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status); Assert.Equal(settings, loaded.Settings);
        var json = File.ReadAllText(fixture.File);
        foreach (var prohibited in new[] { "ApiKey", "Token", "Granted", "Session", "Egress", "Source", "\"Rows\":", "\"Query\":" })
            Assert.DoesNotContain(prohibited, json);
        Assert.Empty(Directory.GetFiles(fixture.Folder, "*.tmp"));
    }

    [Fact]
    public void SwitchingToDemoRemovesSavedProfileAndPreservesBudgets()
    {
        using var fixture = new Fixture(); var settings = AssistantSettings.Default with { AiProvider = Profile, Budgets = new(QueryRows: 2) };
        fixture.Store.Save(settings); fixture.Store.Save(settings with { AiProvider = null });
        Assert.Null(fixture.Store.Load().Settings.AiProvider); Assert.Equal(2, fixture.Store.Load().Settings.Budgets.QueryRows);
        Assert.DoesNotContain("example.invalid", File.ReadAllText(fixture.File));
    }

    [Fact]
    public void LegacyVersionIsMigratedInMemoryWithoutRewritingOriginal()
    {
        using var fixture = new Fixture(); var json = JsonSerializer.Serialize(new { Version = 1, AiProvider = Profile }); fixture.Put(json);
        var loaded = fixture.Store.Load(); Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
        Assert.Equal(2, loaded.Settings.Version); Assert.Equal(Profile, loaded.Settings.AiProvider);
        Assert.Equal(json, File.ReadAllText(fixture.File));
        fixture.Store.Save(loaded.Settings); Assert.Equal(SettingsLoadStatus.Loaded, fixture.Store.Load().Status);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":\"2\"}")]
    [InlineData("{\"Version\":2,\"Version\":2,\"AiProvider\":null,\"Budgets\":{}}")]
    [InlineData("{\"Version\":1,\"AiProvider\":null,\"ApiKey\":\"synthetic-secret\"}")]
    [InlineData("{\"Version\":1,\"AiProvider\":null,\"Granted\":[\"DefinitionWrite\"]}")]
    [InlineData("{\"Version\":2,\"AiProvider\":null}")]
    [InlineData("{\"Version\":2,\"AiProvider\":null,\"Budgets\":null}")]
    [InlineData("{\"Version\":2,\"AiProvider\":null,\"Budgets\":{}}")]
    [InlineData("{\"Version\":1,\"AiProvider\":{\"Id\":\"fixture\",\"Endpoint\":\"http://example.invalid/v1/chat/completions\",\"Model\":\"synthetic\"}}")]
    public void MalformedOrSecretBearingSettingsRemainUntouched(string json)
    {
        using var fixture = new Fixture(); fixture.Put(json);
        Assert.Equal(SettingsLoadStatus.Invalid, fixture.Store.Load().Status);
        Assert.Equal(AssistantSettings.Default, fixture.Store.Load().Settings);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(AssistantSettings.Default));
        Assert.Equal(json, File.ReadAllText(fixture.File));
    }

    [Fact]
    public void FutureVersionIsPreservedAndCannotBeDowngraded()
    {
        using var fixture = new Fixture(); const string json = "{\"Version\":99,\"Future\":true}"; fixture.Put(json);
        Assert.Equal(SettingsLoadStatus.Unsupported, fixture.Store.Load().Status);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(AssistantSettings.Default));
        Assert.Equal(json, File.ReadAllText(fixture.File));
    }

    [Fact]
    public void FailedValidationPreservesPreviouslySavedFile()
    {
        using var fixture = new Fixture(); fixture.Store.Save(AssistantSettings.Default with { AiProvider = Profile });
        var original = File.ReadAllBytes(fixture.File);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(AssistantSettings.Default with { Budgets = new(QueryRows: 0) }));
        Assert.Equal(original, File.ReadAllBytes(fixture.File)); Assert.Empty(Directory.GetFiles(fixture.Folder, "*.tmp"));
    }

    [Fact]
    public void ExclusiveFileLockFailsWithoutReplacingSettings()
    {
        using var fixture = new Fixture(); fixture.Store.Save(AssistantSettings.Default);
        var original = File.ReadAllBytes(fixture.File);
        using (var locked = new FileStream(fixture.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(SettingsLoadStatus.Unavailable, fixture.Store.Load().Status);
            Assert.Throws<InvalidDataException>(() => fixture.Store.Save(AssistantSettings.Default with { AiProvider = Profile }));
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.File));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedOrInvalidUtf8SettingsFailClosed(bool invalidEncoding)
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Folder);
        var bytes = invalidEncoding ? new byte[] { 0xff, 0xfe, 0xff } : Encoding.UTF8.GetBytes(new string('中', 6_000));
        File.WriteAllBytes(fixture.File, bytes); Assert.Equal(SettingsLoadStatus.Invalid, fixture.Store.Load().Status);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.File));
    }

    [Fact]
    public void Utf8BomIsAcceptedForAnOtherwiseValidOwnSettingsFile()
    {
        using var fixture = new Fixture(); fixture.Put("\uFEFF" + JsonSerializer.Serialize(AssistantSettings.Default));
        Assert.Equal(SettingsLoadStatus.Loaded, fixture.Store.Load().Status);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("rows")]
    [InlineData("bytes")]
    [InlineData("query-timeout")]
    [InlineData("rounds")]
    [InlineData("calls")]
    [InlineData("text")]
    [InlineData("turn-timeout")]
    public void BudgetsCannotBypassHardLimits(string kind)
    {
        var budget = kind switch
        {
            "context" => new AssistantBudgets(ContextTables: 21),
            "rows" => new(QueryRows: 101),
            "bytes" => new(QueryBytes: 64_001),
            "query-timeout" => new(QueryTimeoutSeconds: 31),
            "rounds" => new(TurnRounds: 7),
            "calls" => new(TurnToolCalls: 9),
            "text" => new(TurnCharacters: 20_001),
            _ => new(TurnTimeoutSeconds: 121)
        };
        using var fixture = new Fixture();
        var error = Record.Exception(() => fixture.Store.Save(AssistantSettings.Default with { Budgets = budget }));
        Assert.True(error is InvalidDataException or ArgumentOutOfRangeException); Assert.False(Directory.Exists(fixture.Folder));
    }

    [Fact]
    public void SavedTimeoutsRespectTransportAndRoundLimits()
    {
        var settings = new AssistantBudgets(QueryTimeoutSeconds: 20, TurnRounds: 2, TurnTimeoutSeconds: 40);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.QueryLimits(false).EffectiveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.QueryLimits(true).EffectiveTimeout);
        Assert.Equal(2, settings.TurnLimits().MaxRounds); Assert.Equal(TimeSpan.FromSeconds(40), settings.TurnLimits().EffectiveTimeout);
    }

    [Fact]
    public void EndpointValidationIsSharedWithActualProvider()
    {
        Assert.Equal(ProviderProfiles.Validate(Profile), OpenAiCompatibleProvider.ValidateIdentity(Profile));
        Assert.Throws<ArgumentException>(() => ProviderProfiles.Validate(Profile with { Endpoint = "https://example.invalid/v1/chat/completions?key=synthetic" }));
        Assert.Throws<ArgumentException>(() => ProviderProfiles.Validate(Profile with { Id = "bad\nidentifier" }));
        Assert.Throws<ArgumentException>(() => ProviderProfiles.Validate(Profile with
        { Endpoint = "https://example.invalid/" + string.Concat(Enumerable.Repeat("中/", 600)) + "chat/completions" }));
    }

    [Fact]
    public void RelativeAndNetworkStoragePathsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new FileAssistantSettingsStore("relative-folder"));
        if (OperatingSystem.IsWindows()) Assert.Throws<ArgumentException>(() => new FileAssistantSettingsStore(@"\\synthetic.invalid\settings"));
    }

    [Fact]
    public async Task DashboardAlsoUsesTheHostSavedRowBudget()
    {
        var writer = new DemoDefinitionWriter(); var settings = new AssistantBudgets(QueryRows: 2);
        var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Report, [Capability.MetadataRead]);
        var core = new DashboardCoordinator(writer, writer, writer, session, queryLimits: settings.QueryLimits(false));
        var draft = await core.PrepareAsync(DemoDashboard.Plan, session.Snapshot, default);
        session.Update(draft.Definition.Plan.Target, ConversationMode.Report, [Capability.MetadataRead, Capability.DefinitionWrite]);
        var applied = await core.ApplyAsync(draft, core.ApproveAfterUserConfirmation(draft, session.Snapshot), session.Snapshot, default);
        Assert.True(applied.Success);
        session.Update(applied.After!.Identity, ConversationMode.Report, [Capability.MetadataRead, Capability.DataQuery]);
        var result = await core.QueryAsync(draft, applied, session.Snapshot, default);
        Assert.Equal(2, result.Rows.Length); Assert.True(result.Truncated);
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "semantic-settings-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Folder, "settings.json");
        public FileAssistantSettingsStore Store => new(Folder);
        public void Put(string json) { Directory.CreateDirectory(Folder); System.IO.File.WriteAllText(File, json, new UTF8Encoding(false)); }
        public void Dispose()
        {
            // Exact self-created fixture only; no recursive traversal into linked directories.
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: false);
        }
    }
}
