using System.Collections.Immutable;
using System.Text;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Contracts;
using SemanticAssistant.Application;
using SemanticAssistant.Adapters.Demo;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class DesktopDiscoveryTests
{
    private sealed class Inventory : ILocalPortInventory
    {
        public ImmutableArray<LocalPortOwner> Owners = [new(54321, 100, 99)];
        public ImmutableArray<LocalPortOwner> ReadOwners() => Owners;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortFixtureMatchesExactOwnerAndDoesNotConnect(bool unicode)
    {
        var root = Path.Combine(Path.GetTempPath(), "SemanticAssistant-discovery-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "AnalysisServicesWorkspaceSynthetic", "Data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "msmdsrv.port.txt"), "54321\r\n", unicode ? Encoding.Unicode : Encoding.UTF8);
        var inventory = new Inventory(); var discovery = new DesktopDiscovery(inventory);
        var instance = Assert.Single(discovery.Discover([root]));
        Assert.Equal(inventory.Owners[0], instance.Owner); discovery.EnsureCurrent(instance);
        inventory.Owners = [new(54321, 101, 99)];
        Assert.Throws<IOException>(() => discovery.EnsureCurrent(instance));
        inventory.Owners = [new(54321, 100, 100)];
        Assert.Throws<IOException>(() => discovery.EnsureCurrent(instance));
        inventory.Owners = [];
        Assert.Throws<IOException>(() => discovery.EnsureCurrent(instance));
        // Preserve fixture folders for diagnostics; never recursively remove computed paths.
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("54321;Password=secret")]
    public void InvalidPortFilesNeverBecomeConnectionStrings(string text)
    {
        var root = Path.Combine(Path.GetTempPath(), "SemanticAssistant-discovery-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "AnalysisServicesWorkspaceSynthetic", "Data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "msmdsrv.port.txt"), text);
        Assert.Empty(new DesktopDiscovery(new Inventory()).Discover([root]));
    }

    [Theory]
    [InlineData("<Execute><Batch/></Execute>")]
    [InlineData("ALTER TABLE x")]
    [InlineData("CREATE MEMBER x")]
    [InlineData("SELECT * FROM x")]
    [InlineData("EVALUATEX ROW(\"x\", 1)")]
    public void NonDaxEnvelopesAreRejectedBeforeConnection(string text) => Assert.Throws<ToolInputException>(() => ReadOnlyDax.Validate(text));

    [Theory]
    [InlineData("EVALUATE ROW(\"test\", 1)")]
    [InlineData(" DEFINE MEASURE 'x'[a] = 1 EVALUATE ROW(\"test\", [a])")]
    public void DaxQueriesAreAcceptedForServiceSyntaxValidation(string text) => ReadOnlyDax.Validate(text);

    [Fact]
    public async Task DesktopWorkspaceSelectionHasDistinctSourceKind()
    {
        var workspace = new ModelWorkspace(DemoModel.Snapshot);
        await workspace.LoadAsync(new DemoModel(), ModelSourceKind.Desktop, default);
        Assert.Equal(ModelSourceKind.Desktop, workspace.Current.Source); Assert.False(workspace.Current.IsOffline);
        workspace.ResetToDemo(); Assert.Equal(ModelSourceKind.Synthetic, workspace.Current.Source);
    }
}
