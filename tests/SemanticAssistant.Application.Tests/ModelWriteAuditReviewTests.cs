using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ModelWriteAuditReviewTests
{
    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "sa-audit-review-" + Guid.NewGuid().ToString("N"));
        public Guid Operation { get; } = Guid.NewGuid();
        public string File => Path.Combine(Folder, $"operation-{Operation:N}.jsonl");
        public Fixture() => Directory.CreateDirectory(Folder);
        public string Line(string code, Guid? operation = null, string? planHash = null) => JsonSerializer.Serialize(new
        {
            Version = 1, At = DateTimeOffset.UtcNow, OperationId = operation ?? Operation,
            PlanHash = planHash ?? new string('a', 64), SourceHash = new string('b', 64),
            DefinitionRevisionHash = new string('c', 64), IsSynthetic = true, Objects = 1, Code = code
        }) + "\n";
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Folder);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("sa-audit-review-", StringComparison.Ordinal) ||
                new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException();
            Directory.Delete(resolved, true); // Only this newly created test directory, after checking its absolute target.
        }
    }
    [Theory]
    [InlineData("intent", "unresolved", true)]
    [InlineData("intent,verified", "recorded_verified", false)]
    [InlineData("intent,rollback_started", "unresolved", true)]
    [InlineData("intent,rollback_started,failed_rolled_back", "recorded_rolled_back", false)]
    [InlineData("intent,rollback_started,partial_failure", "recorded_partial_failure", true)]
    public void BoundedValidSequencesAreObservationsWithoutModelAuthority(string codes, string status, bool requiresReview)
    {
        using var f = new Fixture(); System.IO.File.WriteAllText(f.File, string.Concat(codes.Split(',').Select(c => f.Line(c))));
        var before = System.IO.File.ReadAllBytes(f.File); var review = ModelWriteAuditReader.Read(f.Folder);
        var entry = Assert.Single(review.Entries);
        Assert.Equal(f.Operation, entry.OperationId); Assert.Equal(status, entry.Status); Assert.Equal(requiresReview, entry.RequiresReview);
        Assert.False(review.ModelStateVerified); Assert.False(review.ModelMutationPerformed); Assert.False(review.AuthorizationsRestored);
        Assert.Equal(before, System.IO.File.ReadAllBytes(f.File));
        Assert.DoesNotContain(new string('a', 64), JsonSerializer.Serialize(review));
    }
    [Theory]
    [InlineData("verified")]
    [InlineData("intent,partial_failure")]
    [InlineData("intent,verified,intent")]
    [InlineData("intent,rollback_started,verified")]
    [InlineData("intent,unrecognized")]
    public void InvalidStateTransitionsRequireReview(string codes)
    {
        using var f = new Fixture(); System.IO.File.WriteAllText(f.File, string.Concat(codes.Split(',').Select(c => f.Line(c))));
        Assert.Equal("unreadable", Assert.Single(ModelWriteAuditReader.Read(f.Folder).Entries).Status);
    }
    [Theory]
    [InlineData("binding")]
    [InlineData("operation")]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    [InlineData("utf8")]
    public void TamperedOrIncompleteEntriesNeverAppearComplete(string kind)
    {
        using var f = new Fixture(); var text = f.Line("intent") + f.Line("verified");
        text = kind switch
        {
            "binding" => f.Line("intent") + f.Line("verified", planHash: new string('d', 64)),
            "operation" => f.Line("intent", Guid.NewGuid()),
            "truncated" => text.TrimEnd('\n'),
            "extra" => text.Replace("\"Version\":1", "\"Version\":1,\"Secret\":\"synthetic-sensitive\""),
            "duplicate" => text.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
            "oversize" => new string('x', 20001), _ => text
        };
        if (kind == "utf8") System.IO.File.WriteAllBytes(f.File, [0xff, 0x0a]); else System.IO.File.WriteAllText(f.File, text);
        var review = ModelWriteAuditReader.Read(f.Folder); var entry = Assert.Single(review.Entries);
        Assert.Equal("unreadable", entry.Status); Assert.True(entry.RequiresReview);
        Assert.DoesNotContain("synthetic-sensitive", JsonSerializer.Serialize(review));
    }
    [Fact]
    public void CancellationAndFileCountLimitStopInspection()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelWriteAuditReader.Read(f.Folder, cancellation.Token));
        for (var i = 0; i < 1001; i++) System.IO.File.WriteAllText(Path.Combine(f.Folder, $"operation-{Guid.NewGuid():N}.jsonl"), "");
        Assert.Throws<InvalidDataException>(() => ModelWriteAuditReader.Read(f.Folder));
    }
    [Fact]
    public void MissingFolderIsNotSilentlyTreatedAsNoPendingWork()
    {
        using var f = new Fixture();
        Assert.Throws<DirectoryNotFoundException>(() => ModelWriteAuditReader.Read(Path.Combine(f.Folder, "missing")));
    }
    [Fact]
    public async Task ActualJournalAfterSuccessfulSyntheticWriteCanBeReadAfterClosingWriter()
    {
        using var f = new Fixture(); var writer = new DemoDefinitionWriter();
        var session = new PermissionSession(DemoModel.Snapshot.Identity, ConversationMode.Analyze, [Capability.MetadataRead, Capability.DefinitionWrite]);
        var core = new ModelWriteCoordinator(writer, session, new FileModelWriteJournal(f.Folder));
        var preview = await core.PrepareAsync([new("銷售", "SA_private_marker", "\"synthetic-sensitive\"")], session.Snapshot, default);
        Assert.True((await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default)).Success);
        var review = ModelWriteAuditReader.Read(f.Folder);
        Assert.Equal("recorded_verified", Assert.Single(review.Entries).Status);
        Assert.DoesNotContain("SA_private_marker", JsonSerializer.Serialize(review));
        Assert.DoesNotContain("synthetic-sensitive", JsonSerializer.Serialize(review));
        Assert.Throws<IOException>(() => new FileModelWriteJournal(f.Folder).Begin(preview));
    }
}
