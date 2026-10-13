using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Cloud;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class QueryResultBufferTests
{
    private const string Dax = "EVALUATE ROW(\"Value\",1)";
    private static QueryResultBuffer Buffer(QueryLimits? limits = null) => new(DemoModel.Snapshot.Identity, ["Value"], Dax, limits ?? new());

    [Fact]
    public void EmptyEnvelopeIsMeasuredEvenWithoutRows()
    {
        Assert.Throws<InvalidDataException>(() => new QueryResultBuffer(DemoModel.Snapshot.Identity, [new string('x', 2000)], Dax, new(MaxBytes:1024)));
        Assert.Throws<InvalidDataException>(() => new QueryResultBuffer(DemoModel.Snapshot.Identity, ["x"], Dax + new string(' ', 2000), new(MaxBytes:1024)));
    }

    [Fact]
    public void ExactRowLimitIsNotTruncationUntilAnotherRowExists()
    {
        var buffer = Buffer(new(MaxRows:1));
        Assert.True(buffer.TryAdd([ContractJson.Element(1)]));
        var first = buffer.Build(); Assert.False(first.Truncated);
        Assert.False(buffer.TryAdd(() => throw new InvalidOperationException("Must not read excess values.")));
        var final = buffer.Build(); Assert.True(final.Truncated); Assert.Single(final.Rows);
        Assert.Equal(first.ResultId, final.ResultId); Assert.Equal(first.ReceivedAt, final.ReceivedAt);
        Assert.Equal(first.ReceivedTimestamp, final.ReceivedTimestamp);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("合")]
    [InlineData("\"")]
    public void Utf8AndEscapesFitTheCompleteFinalEnvelope(string unit)
    {
        var buffer = Buffer(new(MaxBytes:1024));
        var value = string.Concat(Enumerable.Repeat(unit, 180));
        for (var i = 0; i < 20; i++) if (!buffer.TryAdd([ContractJson.Element(value)])) break;
        var result = buffer.Build(); Assert.True(result.Truncated);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(result).Length, 1, 1024);
        Assert.Equal(DemoModel.Snapshot.Identity, result.Source); Assert.Equal(Dax, result.Query);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ComplexCellCannotBePublished(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => Buffer().TryAdd([document.RootElement]));
    }

    [Fact]
    public void InvalidWidthAndUndefinedRowAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Buffer().TryAdd(ImmutableArray<JsonElement>.Empty));
        Assert.Throws<InvalidDataException>(() => Buffer().TryAdd(default(ImmutableArray<JsonElement>)));
        Assert.Throws<InvalidDataException>(() => Buffer().TryAdd([default(JsonElement)]));
        Assert.Throws<InvalidDataException>(() => new QueryResultBuffer(DemoModel.Snapshot.Identity, [], Dax, new()).TryAdd(ImmutableArray<JsonElement>.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloudEmptyResponseCannotBypassQueryOrHeaderBudget(bool rows)
    {
        using var json = JsonDocument.Parse(rows ? "{\"results\":[{\"tables\":[{\"rows\":[{\"x\":1}]}]}]}" : "{\"results\":[{\"tables\":[{\"rows\":[]}]}]}");
        Assert.Throws<CloudServiceException>(() => CloudModelAdapter.ParseQueryResponse(json.RootElement, DemoModel.Snapshot.Identity,
            Dax + new string(' ', 2000), new(MaxBytes:1024)));
    }

    [Fact]
    public async Task CancelledDiscoveryFailureIsNormalizedBeforeAnySdkConnection()
    {
        using var cancellation = new CancellationTokenSource();
        var inventory = new FailingInventory(() => cancellation.Cancel());
        var adapter = new DesktopModelAdapter(new("unused", new(54321,1,1)), "unused", new(inventory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteAsync(DemoModel.Snapshot.Identity, Dax, new(), cancellation.Token));
        Assert.Equal(1, inventory.Calls);
    }

    [Fact]
    public async Task AdapterOwnDeadlineRejectsDirectCallsWithoutDispatcher()
    {
        var inventory = new FailingInventory(() => Thread.Sleep(50));
        var adapter = new DesktopModelAdapter(new("unused", new(54321,1,1)), "unused", new(inventory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteAsync(DemoModel.Snapshot.Identity, Dax,
            new(Timeout:TimeSpan.FromMilliseconds(1)), default));
        Assert.InRange(inventory.Calls, 0, 1);
    }

    private sealed class FailingInventory(Action fail) : ILocalPortInventory
    {
        public int Calls;
        public ImmutableArray<LocalPortOwner> ReadOwners() { Calls++; fail(); throw new IOException("No SDK connection."); }
    }
}
