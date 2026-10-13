using System.Collections.Immutable;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Cloud;
using SemanticAssistant.Application;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class CloudWriterAuthorizationTests
{
    [Fact]
    public async Task PreviewUsesReadScopeApplyCanRequestWriteAndRestoreCannotPrompt()
    {
        var identity = new ProbeIdentity();
        using var model = new CloudModelAdapter(identity, Guid.NewGuid(), new(Guid.NewGuid(), "SyntheticWorkspace"), new(Guid.NewGuid(), "SyntheticDataset"));
        var writer = model.CreateWriter();
        await Assert.ThrowsAsync<IOException>(() => writer.ReadDefinitionAsync(default));
        Assert.Equal(1, identity.Reads); Assert.Empty(identity.WriteRequests);
        var plan = new DefinitionPlan(Guid.NewGuid(), DemoModel.Snapshot.Identity, "test-only", DemoDashboard.Plan.Metrics);
        await Assert.ThrowsAsync<IOException>(() => writer.ApplyAsync(plan, () => { }, default));
        Assert.Equal([true], identity.WriteRequests);
        var state = new DefinitionState(plan.Target, "test-only", ImmutableArray<DefinitionTable>.Empty);
        var preview = new DefinitionPreview(Guid.NewGuid(), plan, DefinitionRules.HashPlan(plan), state, "synthetic");
        await Assert.ThrowsAsync<IOException>(() => writer.RestoreAsync(preview, state, default));
        Assert.Equal([true, false], identity.WriteRequests); Assert.Equal(1, identity.Reads);
        // The token source fails before any Server is constructed or connected.
    }
    private sealed class ProbeIdentity : IDelegatedTokenSource, IModelWriteTokenSource
    {
        public int Reads;
        public List<bool> WriteRequests { get; } = [];
        public Task<DelegatedToken> AcquireAsync(CancellationToken token) { Reads++; return Task.FromException<DelegatedToken>(new IOException()); }
        public Task<DelegatedToken> AcquireWriteAsync(bool allowInteractive, CancellationToken token)
        { WriteRequests.Add(allowInteractive); return Task.FromException<DelegatedToken>(new IOException()); }
    }
}
