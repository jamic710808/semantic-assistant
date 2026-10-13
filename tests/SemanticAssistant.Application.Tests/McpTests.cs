using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.Mcp;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class McpTests
{
    private static McpProcessConfiguration Configuration(string mode = "normal")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticAssistant.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new IOException("Fixture source root not found.");
        var host = System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var fixture = Path.Combine(root, "tests", "SemanticAssistant.FixtureHost", "bin", "Release", "net10.0", "SemanticAssistant.FixtureHost.dll");
        return new(host, [fixture, mode]);
    }
    private static PermissionSession Session(bool grant = true) => new(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
        grant ? ImmutableHashSet.Create(Capability.ExternalTool) : ImmutableHashSet<Capability>.Empty);
    private static JsonElement Arguments(string message = "合成訊息") => ContractJson.Element(new { message });

    [Fact]
    public async Task ActualChildProcessInitializesListsAndExecutesApprovedTool()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(), default);
        var tool = Assert.Single(await client.ListToolsAsync(default));
        Assert.Equal(Capability.ExternalTool, tool.RequiredCapability); Assert.Contains("PID", tool.Source);
        var session = Session(); var args = Arguments();
        var approval = await client.ApproveAfterUserConfirmationAsync(tool, args, session, session.Snapshot, default);
        var result = await client.CallToolAsync(tool, args, approval, session, session.Snapshot, default);
        Assert.Equal("合成結果：合成訊息", result.Content[0].GetProperty("text").GetString());
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, args, approval, session, session.Snapshot, default));
    }

    [Fact]
    public async Task PermissionAndPerCallApprovalAreBothRequired()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(), default);
        var tool = Assert.Single(await client.ListToolsAsync(default)); var noGrant = Session(false);
        await Assert.ThrowsAsync<McpException>(() => client.ApproveAfterUserConfirmationAsync(tool, Arguments(), noGrant, noGrant.Snapshot, default));
        var session = Session();
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, Arguments(), null, session, session.Snapshot, default));
    }

    [Fact]
    public async Task ChangedArgumentsRevocationOrCatalogCannotReuseApproval()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(), default);
        var tool = Assert.Single(await client.ListToolsAsync(default)); var session = Session(); var pinned = session.Snapshot;
        var approval = await client.ApproveAfterUserConfirmationAsync(tool, Arguments(), session, pinned, default);
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, Arguments("changed"), approval, session, pinned, default));
        session.Update(pinned.Target, pinned.Mode, pinned.Granted);
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, Arguments(), approval, session, pinned, default));
        await client.ListToolsAsync(default);
        await Assert.ThrowsAsync<McpException>(() => client.ApproveAfterUserConfirmationAsync(tool, Arguments(), session, session.Snapshot, default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":4}")]
    [InlineData("{\"message\":\"x\",\"extra\":true}")]
    [InlineData("{\"message\":\"x\",\"message\":\"y\"}")]
    public async Task InvalidSchemaArgumentsAreRejectedBeforeToolsCall(string json)
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(), default);
        var tool = Assert.Single(await client.ListToolsAsync(default)); var session = Session();
        using var arguments = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<McpException>(() => client.ApproveAfterUserConfirmationAsync(tool, arguments.RootElement, session, session.Snapshot, default));
    }

    [Theory]
    [InlineData("bad-json")]
    [InlineData("oversize")]
    [InlineData("remote-ref")]
    public async Task InvalidCatalogOrRemoteSchemaIsRejected(string mode)
    {
        await using var client = await McpStdioClient.StartAsync(Configuration(mode), default);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ListToolsAsync(default));
    }

    [Fact]
    public async Task UnknownProtocolIsRejected() => await Assert.ThrowsAsync<McpException>(() => McpStdioClient.StartAsync(Configuration("bad-version"), default));

    [Fact]
    public async Task ListChangedDuringCallDiscardsResult()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration("change"), default);
        var tool = Assert.Single(await client.ListToolsAsync(default)); var session = Session(); var args = Arguments();
        var approval = await client.ApproveAfterUserConfirmationAsync(tool, args, session, session.Snapshot, default);
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, args, approval, session, session.Snapshot, default));
    }

    [Fact]
    public async Task CancellationDoesNotRetryUncertainExternalCall()
    {
        await using var client = await McpStdioClient.StartAsync(Configuration("hang"), default);
        var tool = Assert.Single(await client.ListToolsAsync(default)); var session = Session(); var args = Arguments();
        var approval = await client.ApproveAfterUserConfirmationAsync(tool, args, session, session.Snapshot, default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, args, approval, session, session.Snapshot, cancel.Token));
        Assert.False(client.IsAlive);
    }

    [Fact]
    public async Task RemoteErrorAndEnvironmentAreRedacted()
    {
        var configuration = Configuration("remote-error");
        Assert.DoesNotContain("remote-error", configuration.ToString());
        await using var client = await McpStdioClient.StartAsync(configuration, default);
        var tool = Assert.Single(await client.ListToolsAsync(default)); var session = Session(); var args = Arguments();
        var approval = await client.ApproveAfterUserConfirmationAsync(tool, args, session, session.Snapshot, default);
        var error = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tool, args,
            approval, session, session.Snapshot, default));
        Assert.DoesNotContain("synthetic-sensitive", error.Message);
    }
}
