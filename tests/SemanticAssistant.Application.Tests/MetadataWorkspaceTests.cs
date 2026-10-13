using System.Collections.Immutable;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class MetadataWorkspaceTests
{
    private static ModelSnapshot Offline(string revision = "file-v1") => DemoModel.Snapshot with
    {
        Identity = new("file-model", "離線測試模型", revision, false),
        Relationships = [new("測試關係", "銷售", "日期", "日期", "月份", true)]
    };

    [Fact]
    public async Task SuccessfulReadCommitsOfflineModel()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        Assert.True(await workspace.LoadOfflineAsync(new FixedReader(Offline()), default));
        Assert.True(workspace.Current.IsOffline);
        Assert.Equal("file-v1", workspace.Current.Model.Identity.Revision);
        workspace.ResetToDemo();
        Assert.False(workspace.Current.IsOffline);
        Assert.Same(DemoModel.Snapshot, workspace.Current.Model);
    }

    [Fact]
    public async Task FailurePreservesPreviouslySelectedModel()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        await workspace.LoadOfflineAsync(new FixedReader(Offline()), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.LoadOfflineAsync(new FailedReader(), default));
        Assert.Equal("file-v1", workspace.Current.Model.Identity.Revision);
        Assert.True(workspace.Current.IsOffline);
    }

    [Fact]
    public async Task CancellationDiscardsNonCooperativeLateRead()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        var reader = new PendingReader();
        using var cancellation = new CancellationTokenSource();
        var loading = workspace.LoadOfflineAsync(reader, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        reader.Complete(Offline());
        Assert.False(workspace.Current.IsOffline);
        Assert.Same(DemoModel.Snapshot, workspace.Current.Model);
    }

    [Fact]
    public async Task ResetInvalidatesPendingSelection()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        var reader = new PendingReader();
        var loading = workspace.LoadOfflineAsync(reader, default);
        workspace.ResetToDemo();
        reader.Complete(Offline());
        Assert.False(await loading);
        Assert.False(workspace.Current.IsOffline);
    }

    [Fact]
    public async Task NewerSelectionWinsOverEarlierLateRead()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        var reader = new PendingReader();
        var oldLoading = workspace.LoadOfflineAsync(reader, default);
        await workspace.LoadOfflineAsync(new FixedReader(Offline("file-v2")), default);
        reader.Complete(Offline());
        Assert.False(await oldLoading);
        Assert.Equal("file-v2", workspace.Current.Model.Identity.Revision);
    }

    [Fact]
    public async Task ReloadAcceptsChangedRevision()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        await workspace.LoadOfflineAsync(new FixedReader(Offline()), default);
        await workspace.LoadOfflineAsync(new FixedReader(Offline("file-v2")), default);
        Assert.Equal("file-v2", workspace.Current.Model.Identity.Revision);
    }

    [Fact]
    public void InspectorShowsDaxAndRelationshipWithoutCalculatingRows()
    {
        var nodes = Flatten(MetadataInspector.Build(Offline())).ToArray();
        Assert.Contains(nodes, n => n.Id == "table/" + Uri.EscapeDataString("銷售") + "/measure/" + Uri.EscapeDataString("銷售額") && n.Detail.Contains("SUM("));
        var relationship = nodes.Single(n => n.Id.StartsWith("relationship/", StringComparison.Ordinal));
        Assert.Contains("銷售[日期]", relationship.Detail);
        Assert.Contains("日期[月份]", relationship.Detail);
        Assert.DoesNotContain(nodes, n => n.Detail.Contains("120000"));
    }

    [Fact]
    public void SearchKeepsParentPathAndLeavesSnapshotUnchanged()
    {
        var model = Offline();
        var nodes = Flatten(MetadataInspector.Build(model, "銷售額")).ToArray();
        Assert.Contains(nodes, n => n.Title.StartsWith("∑", StringComparison.Ordinal));
        Assert.Contains(nodes, n => n.Id == "tables");
        Assert.DoesNotContain(nodes, n => n.Id == "table/" + Uri.EscapeDataString("日期"));
        Assert.Equal(2, model.Tables.Length);
        Assert.Empty(MetadataInspector.Build(model, "no-such-object"));
    }

    [Fact]
    public void InspectorBoundsVeryLargeDaxForDisplay()
    {
        var model = new ModelSnapshot(Offline().Identity,
            [new("table", [], [new("big", new string('x', 25_000))])]);
        var node = Flatten(MetadataInspector.Build(model)).Single(n => n.Title == "∑ big");
        Assert.Contains("顯示已截斷", node.Detail);
        Assert.True(node.Detail.Length < 21_000);
        Assert.Equal(25_000, model.Tables[0].Measures[0].Expression.Length);
    }

    private static IEnumerable<MetadataNode> Flatten(ImmutableArray<MetadataNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private sealed class FixedReader(ModelSnapshot snapshot) : ISemanticModelReader
    {
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
    private sealed class FailedReader : ISemanticModelReader
    {
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<ModelSnapshot>(new InvalidDataException("synthetic failure"));
    }
    private sealed class PendingReader : ISemanticModelReader
    {
        private readonly TaskCompletionSource<ModelSnapshot> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => pending.Task;
        public void Complete(ModelSnapshot snapshot) => pending.SetResult(snapshot);
    }
}
