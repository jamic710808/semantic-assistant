using System.Collections.Immutable;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.PowerBIFixture;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class PinnedFixturePortInventoryTests
{
    [Fact]
    public void OtherEnginesAndReusedPidsAreExcluded()
    {
        var selected = PinnedFixturePortInventory.SelectOwned([new(1234, 42, 100), new(2345, 43, 100), new(3456, 42, 101)], 42, 100);
        Assert.Equal(new LocalPortOwner(1234, 42, 100), Assert.Single(selected));
    }
    [Fact]
    public void NoMatchCannotFallBackToAnotherInstance() => Assert.Throws<InvalidDataException>(() =>
        PinnedFixturePortInventory.SelectOwned([new(2345, 43, 100)], 42, 100));
    [Theory]
    [InlineData(0, 100, 1234)]
    [InlineData(42, 0, 1234)]
    [InlineData(42, 100, 0)]
    [InlineData(42, 100, 65536)]
    public void InvalidPinsOrPortsDenied(int pid, long ticks, int port) => Assert.Throws<InvalidDataException>(() =>
        PinnedFixturePortInventory.SelectOwned([new(port, pid, ticks)], pid, ticks));
    [Fact]
    public void OwnershipIsRecheckedAfterPortRead()
    {
        var checks = 0;
        var inventory = new PinnedFixturePortInventory(new FakeInventory(), 42, 100,
            () => { if (++checks == 2) throw new InvalidDataException(); });
        Assert.Throws<InvalidDataException>(() => inventory.ReadOwners());
        Assert.Equal(2, checks);
    }
    private sealed class FakeInventory : ILocalPortInventory
    { public ImmutableArray<LocalPortOwner> ReadOwners() => [new(1234, 42, 100)]; }
}
