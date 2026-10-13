using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Cloud;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class CloudQueryDeadlineTests
{
    private const string Dax = "EVALUATE ROW(\"a\",1)";
    private static QueryLimits Limits => new(Timeout: TimeSpan.FromSeconds(10));
    private static JsonDocument Response() => JsonDocument.Parse("""{"results":[{"tables":[{"rows":[{"a":1}]}]}]}""");
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Expire() => ticks += TimeSpan.FromSeconds(11).Ticks;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task MonotonicDeadlineRejectsLateReturnOrErrorBeforeTimerCallback(int phase, bool throws)
    {
        var clock = new Clock(); var reads = 0; var requests = 0;
        void Delay(int stage, CancellationToken token)
        {
            if (stage != phase) return;
            // Advance only monotonic time: the real timer has not fired.
            Assert.False(token.IsCancellationRequested); clock.Expire();
            Assert.False(token.IsCancellationRequested);
            if (throws) throw new IOException("synthetic internal error");
        }
        Task<ModelSnapshot> Read(CancellationToken token)
        { reads++; Delay(reads == 1 ? 1 : 3, token); return Task.FromResult(DemoModel.Snapshot); }
        Task<JsonDocument> Execute(CancellationToken token)
        { requests++; Delay(2, token); return Task.FromResult(Response()); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudQueryExecution.ExecuteAsync(
            DemoModel.Snapshot.Identity, Dax, Limits, Read, Execute, default, clock));
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Equal(phase == 3 ? 2 : 1, reads);
        Assert.Equal(phase == 1 ? 0 : 1, requests);
        Assert.DoesNotContain("synthetic internal error", error.Message);
    }

    [Fact]
    public async Task PreCancelledCallDoesNotReadSnapshotOrRequestRest()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var reads = 0; var requests = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudQueryExecution.ExecuteAsync(
            DemoModel.Snapshot.Identity, Dax, Limits,
            _ => { reads++; return Task.FromResult(DemoModel.Snapshot); },
            _ => { requests++; return Task.FromResult(Response()); }, cancellation.Token));
        Assert.Equal(0, reads); Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DependenciesIgnoringCallerCancellationCannotPublishResult(int phase)
    {
        using var cancellation = new CancellationTokenSource(); var reads = 0; var requests = 0;
        Task<ModelSnapshot> Read(CancellationToken token)
        { reads++; if ((reads == 1 ? 1 : 3) == phase) cancellation.Cancel(); return Task.FromResult(DemoModel.Snapshot); }
        Task<JsonDocument> Execute(CancellationToken token)
        { requests++; if (phase == 2) cancellation.Cancel(); return Task.FromResult(Response()); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CloudQueryExecution.ExecuteAsync(
            DemoModel.Snapshot.Identity, Dax, Limits, Read, Execute, cancellation.Token));
        Assert.Equal(phase == 3 ? 2 : 1, reads); Assert.Equal(phase == 1 ? 0 : 1, requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChangedSnapshotBeforeOrAfterRestRejectsResult(int stage)
    {
        var reads = 0; var requests = 0;
        Task<ModelSnapshot> Read(CancellationToken token)
        {
            reads++;
            return Task.FromResult(reads == stage ? DemoModel.Snapshot with
            { Identity = DemoModel.Snapshot.Identity with { Revision = "changed" } } : DemoModel.Snapshot);
        }
        await Assert.ThrowsAsync<CloudServiceException>(() => CloudQueryExecution.ExecuteAsync(
            DemoModel.Snapshot.Identity, Dax, Limits, Read,
            _ => { requests++; return Task.FromResult(Response()); }, default));
        Assert.Equal(stage == 1 ? 0 : 1, requests);
    }

    [Fact]
    public async Task SuccessfulQueryRequiresBothSnapshotsAndDisposesResponse()
    {
        var reads = 0; var response = Response();
        var result = await CloudQueryExecution.ExecuteAsync(DemoModel.Snapshot.Identity, Dax, Limits,
            _ => { reads++; return Task.FromResult(DemoModel.Snapshot); }, _ => Task.FromResult(response), default);
        Assert.Equal(2, reads); Assert.Equal(1, Assert.Single(result.Rows)[0].GetInt32());
        Assert.Equal(DemoModel.Snapshot.Identity, result.Source);
        Assert.Throws<ObjectDisposedException>(() => response.RootElement.GetRawText());
    }

    [Fact]
    public async Task FailureBeforeDeadlineIsNotMisreportedAsCancellation()
    {
        await Assert.ThrowsAsync<IOException>(() => CloudQueryExecution.ExecuteAsync(DemoModel.Snapshot.Identity, Dax, Limits,
            _ => Task.FromException<ModelSnapshot>(new IOException()), _ => Task.FromResult(Response()), default));
    }
}
