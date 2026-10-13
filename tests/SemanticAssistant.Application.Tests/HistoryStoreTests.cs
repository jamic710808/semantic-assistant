using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticAssistant.Application;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class HistoryStoreTests
{
    [Fact]
    public void DisabledByDefaultDoesNotCreateFiles()
    {
        using var f = new Fixture(); var store = f.Store;
        Assert.Equal(HistoryLoadStatus.Missing, store.Load().Status);
        store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, 2);
        store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, "not-a-path");
        Assert.False(Directory.Exists(f.Folder));
    }
    [Fact]
    public void RestartContainsOnlyTypedSummaryAndOwnArtifactName()
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(true, true));
        f.Store.RecordTurn(AssistantMode.Report, ModelSourceKind.Synthetic, HistoryOutcome.Success, 2);
        f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, f.Artifact());
        var loaded = f.Store.Load(); Assert.Equal(HistoryLoadStatus.Loaded, loaded.Status); Assert.Equal(2, loaded.Snapshot.Entries.Length);
        Assert.Single(loaded.Snapshot.Entries, e => e.ArtifactFileName is not null);
        var json = File.ReadAllText(f.File);
        foreach (var prohibited in new[] { "synthetic-secret", "397000", "EVALUATE", "Message", "ModelIdentity", "ApiKey", "Token", "Granted", "Egress", "Query", "Rows", f.Root })
            Assert.DoesNotContain(prohibited, json);
        Assert.Empty(Directory.GetFiles(f.Folder, "*.tmp"));
    }
    [Fact]
    public void SeparateOptInsAndDisablePruneOnlyIndexNotReports()
    {
        using var f = new Fixture(); var path = f.Artifact(); f.Store.SetPolicy(new(true, false));
        f.Store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, 0);
        f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, path);
        Assert.Single(f.Store.Load().Snapshot.Entries);
        f.Store.SetPolicy(new(false, true)); Assert.Empty(f.Store.Load().Snapshot.Entries);
        f.Store.RecordTurn(AssistantMode.Report, ModelSourceKind.Synthetic, HistoryOutcome.Success, 2);
        f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, path); Assert.Single(f.Store.Load().Snapshot.Entries);
        f.Store.SetPolicy(new()); Assert.Empty(f.Store.Load().Snapshot.Entries); Assert.True(File.Exists(path));
    }
    [Fact]
    public void RemoveAndClearPreservePolicyAndArtifact()
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(true, true)); var path = f.Artifact();
        f.Store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Cancelled, 0);
        f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, path);
        var id = f.Store.Load().Snapshot.Entries.Single(e => e.ArtifactFileName is null).Id;
        f.Store.Remove(id); Assert.Single(f.Store.Load().Snapshot.Entries);
        f.Store.Clear(); Assert.Empty(f.Store.Load().Snapshot.Entries); Assert.True(f.Store.Load().Snapshot.Policy.SaveArtifacts); Assert.True(File.Exists(path));
    }
    [Fact]
    public void RetentionBoundaryAndCapacityPruneWithoutReadingArtifactContent()
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(true, false, 1, 2));
        for (var i = 0; i < 3; i++) { f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, i); }
        Assert.Equal(new[] { 2, 1 }, f.Store.Load().Snapshot.Entries.Select(e => e.ToolCalls));
        f.Clock.Advance(TimeSpan.FromDays(1)); Assert.Empty(f.Store.Load().Snapshot.Entries);
        Assert.Equal(2, StrictJson.Parse<HistorySnapshot>(File.ReadAllText(f.File)).Entries.Length); // read-only filtering
        f.Store.RecordTurn(AssistantMode.Report, ModelSourceKind.Synthetic, HistoryOutcome.Success, 0);
        Assert.Single(StrictJson.Parse<HistorySnapshot>(File.ReadAllText(f.File)).Entries);
    }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(31, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void InvalidPolicyLeavesOriginalIntact(int days, int maximum)
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(true)); var bytes = File.ReadAllBytes(f.File);
        Assert.Throws<InvalidDataException>(() => f.Store.SetPolicy(new(true, false, days, maximum)));
        Assert.Equal(bytes, File.ReadAllBytes(f.File));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Policy\":{},\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Policy\":null,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Policy\":{\"SaveSummaries\":true,\"SaveArtifacts\":false,\"RetentionDays\":7,\"MaximumEntries\":50},\"Entries\":[],\"Rows\":[397000]}")]
    public void InvalidOrSecretBearingJsonIsPreserved(string json)
    {
        using var f = new Fixture(); f.Put(json); Assert.Equal(HistoryLoadStatus.Invalid, f.Store.Load().Status);
        Assert.Throws<InvalidDataException>(() => f.Store.Clear()); Assert.Throws<InvalidDataException>(() => f.Store.SetPolicy(new()));
        Assert.Equal(json, File.ReadAllText(f.File));
    }
    [Fact]
    public void FutureVersionCannotBeDowngraded()
    {
        using var f = new Fixture(); const string json = "{\"Version\":99,\"Future\":true}"; f.Put(json);
        Assert.Equal(HistoryLoadStatus.Unsupported, f.Store.Load().Status);
        Assert.Throws<InvalidDataException>(() => f.Store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, 0));
        Assert.Equal(json, File.ReadAllText(f.File));
    }
    [Fact]
    public void InvalidEncodingAndOversizedFileAreRejectedWithoutReplacement()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Folder); File.WriteAllBytes(f.File, [0xff, 0xff]);
        Assert.Equal(HistoryLoadStatus.Invalid, f.Store.Load().Status); Assert.Throws<InvalidDataException>(() => f.Store.Clear());
        var bytes = new byte[128001]; File.WriteAllBytes(f.File, bytes);
        Assert.Equal(HistoryLoadStatus.Invalid, f.Store.Load().Status); Assert.Equal(bytes, File.ReadAllBytes(f.File));
    }
    [Fact]
    public void IndependentStoresReReadLatestIndexAndRespectExclusiveMutationLock()
    {
        using var f = new Fixture(); var one = f.Store; var two = f.Store; one.SetPolicy(new(true));
        one.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, 1);
        two.RecordTurn(AssistantMode.Report, ModelSourceKind.Synthetic, HistoryOutcome.Success, 2);
        Assert.Equal(2, one.Load().Snapshot.Entries.Length); var before = File.ReadAllBytes(f.File);
        using (var locked = new FileStream(Path.Combine(f.Folder, "history.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => one.Clear());
        Assert.Equal(before, File.ReadAllBytes(f.File));
    }
    [Theory]
    [InlineData("../outside.html")]
    [InlineData("report.html")]
    [InlineData("report-20261013-010000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.html/extra")]
    public void ArtifactTraversalAndUnknownNamesAreRejected(string name)
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(false, true));
        Assert.Throws<InvalidDataException>(() => f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, Path.Combine(f.Reports, name)));
        Assert.Empty(f.Store.Load().Snapshot.Entries);
    }
    [Fact]
    public void ArtifactMustExistInsideOwnReportsAndMissingLaterDoesNotRunAnything()
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(false, true));
        var path = f.Artifact(); var name = Path.GetFileName(path);
        Assert.Throws<InvalidDataException>(() => f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, Path.Combine(f.Root, name)));
        f.Store.RecordArtifact(AssistantMode.HtmlFile, ModelSourceKind.Synthetic, path); var entry = Assert.Single(f.Store.Load().Snapshot.Entries);
        File.Delete(path); Assert.Equal(path, f.Store.ArtifactLocation(entry)); Assert.False(File.Exists(path));
    }
    [Theory]
    [InlineData("Mode", 99)]
    [InlineData("Source", 99)]
    [InlineData("Outcome", 99)]
    [InlineData("ToolCalls", 9)]
    public void ForgedTypedSummaryIsRejected(string field, int value)
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(true)); f.Store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, 0);
        var node = JsonNode.Parse(File.ReadAllText(f.File))!; node["Entries"]![0]![field] = value; var json = node.ToJsonString(); f.Put(json);
        Assert.Equal(HistoryLoadStatus.Invalid, f.Store.Load().Status); Assert.Throws<InvalidDataException>(() => f.Store.Clear()); Assert.Equal(json, File.ReadAllText(f.File));
    }
    [Fact]
    public void DuplicateIdsAndFutureTimestampsAreRejected()
    {
        using var f = new Fixture(); f.Store.SetPolicy(new(true)); f.Store.RecordTurn(AssistantMode.Analyze, ModelSourceKind.Synthetic, HistoryOutcome.Success, 0);
        var node = JsonNode.Parse(File.ReadAllText(f.File))!; var entries = (JsonArray)node["Entries"]!; entries.Add(entries[0]!.DeepClone()); f.Put(node.ToJsonString());
        Assert.Equal(HistoryLoadStatus.Invalid, f.Store.Load().Status);
        entries.RemoveAt(1); entries[0]!["CreatedAt"] = f.Clock.GetUtcNow().AddHours(1); f.Put(node.ToJsonString());
        Assert.Equal(HistoryLoadStatus.Invalid, f.Store.Load().Status);
    }
    [Fact]
    public void UnknownInputModesNeverProduceHistory()
    {
        using var f = new Fixture(); Assert.Throws<InvalidDataException>(() => f.Store.RecordTurn((AssistantMode)99, ModelSourceKind.Synthetic, HistoryOutcome.Success, 0));
        Assert.False(Directory.Exists(f.Folder));
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "semantic-history-" + Guid.NewGuid().ToString("N"));
        public string Folder => Path.Combine(Root, "history"); public string Reports => Path.Combine(Root, "reports");
        public string File => Path.Combine(Folder, "history.json"); public Clock Clock { get; } = new();
        public FileHistoryStore Store => new(Folder, Reports, Clock);
        public void Put(string json) { Directory.CreateDirectory(Folder); System.IO.File.WriteAllText(File, json, new UTF8Encoding(false)); }
        public string Artifact()
        {
            Directory.CreateDirectory(Reports); var path = Path.Combine(Reports, "report-20261013-010000-" + Guid.NewGuid().ToString("N") + ".html");
            System.IO.File.WriteAllText(path, "synthetic-secret 397000 EVALUATE fixture", new UTF8Encoding(false)); return path;
        }
        public void Dispose()
        {
            // Only exact self-created fixture files; directories are removed non-recursively.
            if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
            var lockFile = Path.Combine(Folder, "history.lock"); if (System.IO.File.Exists(lockFile)) System.IO.File.Delete(lockFile);
            if (Directory.Exists(Reports)) { foreach (var path in Directory.GetFiles(Reports, "report-20261013-010000-*.html")) System.IO.File.Delete(path); Directory.Delete(Reports, false); }
            if (Directory.Exists(Folder)) Directory.Delete(Folder, false); if (Directory.Exists(Root)) Directory.Delete(Root, false);
        }
    }
}
