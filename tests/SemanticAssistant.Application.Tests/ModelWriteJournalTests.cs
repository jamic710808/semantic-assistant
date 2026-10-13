using System.Collections.Immutable;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class ModelWriteJournalTests
{
    private static PermissionSession Session() => new(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
        ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
    [Fact]
    public async Task IntentAndVerifiedAreDurableWithoutNamesExpressionsOrData()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sa-journal-" + Guid.NewGuid().ToString("N"));
        var writer = new DemoDefinitionWriter(); var session = Session(); var journal = new FileModelWriteJournal(folder); var core = new ModelWriteCoordinator(writer, session, journal);
        var preview = await core.PrepareAsync([new("銷售", "SA_private_marker", "\"synthetic-secret\"")], session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default); Assert.True(outcome.Success);
        var log = File.ReadAllText(Directory.GetFiles(folder).Single());
        Assert.Contains("\"Code\":\"intent\"", log); Assert.Contains("\"Code\":\"verified\"", log);
        Assert.Contains(preview.Plan.OperationId.ToString(), log); Assert.DoesNotContain("SA_private_marker", log); Assert.DoesNotContain("synthetic-secret", log); Assert.DoesNotContain("demo-sales", log);
        Assert.Throws<IOException>(() => journal.Begin(preview));
    }
    [Fact]
    public async Task AuditCreationFailurePreventsAnyMutation()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session, new BrokenJournal());
        var preview = await core.PrepareAsync(DemoDashboard.Plan.Metrics, session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default);
        Assert.False(outcome.Success); Assert.Equal("write_denied", outcome.Code);
        Assert.Equal("demo-definition-0", (await writer.ReadDefinitionAsync(default)).Revision);
    }
    [Fact]
    public async Task AuditCompletionFailureDoesNotUndoAnAlreadyVerifiedWrite()
    {
        var writer = new DemoDefinitionWriter(); var session = Session(); var core = new ModelWriteCoordinator(writer, session, new BrokenJournal(true));
        var preview = await core.PrepareAsync(DemoDashboard.Plan.Metrics, session.Snapshot, default);
        var outcome = await core.ApplyAsync(preview, core.ApproveAfterUserConfirmation(preview, session.Snapshot), session.Snapshot, default);
        Assert.True(outcome.Success); Assert.Equal("verified_journal_incomplete", outcome.Code);
        Assert.True(DefinitionRules.MatchesApplied(preview.Plan, await writer.ReadDefinitionAsync(default)));
    }
    private sealed class BrokenJournal(bool afterBegin = false) : IModelWriteJournal
    {
        public IModelWriteAudit Begin(DefinitionPreview preview) => afterBegin ? new BrokenAudit() : throw new IOException("synthetic-secret");
        private sealed class BrokenAudit : IModelWriteAudit { public void Record(string code) => throw new IOException(); public void Dispose() { } }
    }
}
