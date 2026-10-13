using System.Collections.Immutable;
using SemanticAssistant.Adapters.PowerBI.Local;

namespace SemanticAssistant.PowerBIFixture;

public sealed class PinnedFixturePortInventory(ILocalPortInventory underlying, int processId, long startedUtcTicks,
    Action ensureOwned) : ILocalPortInventory
{
    public ImmutableArray<LocalPortOwner> ReadOwners()
    {
        ensureOwned();
        var selected = SelectOwned(underlying.ReadOwners(), processId, startedUtcTicks);
        ensureOwned();
        return selected;
    }

    public static ImmutableArray<LocalPortOwner> SelectOwned(ImmutableArray<LocalPortOwner> owners, int processId, long startedUtcTicks)
    {
        if (processId <= 0 || startedUtcTicks <= 0 || owners.IsDefault) throw new InvalidDataException("Owned engine pin invalid.");
        var selected = owners.Where(o => o.ProcessId == processId && o.StartedUtcTicks == startedUtcTicks).Distinct().ToImmutableArray();
        if (selected.IsEmpty || selected.Any(o => o.Port is < 1 or > 65535)) throw new InvalidDataException("Owned engine unavailable; no instance fallback.");
        return selected;
    }
}
