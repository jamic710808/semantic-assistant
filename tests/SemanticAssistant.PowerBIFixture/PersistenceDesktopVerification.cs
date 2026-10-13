using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

// Read-only acceptance after a user/UI saves and reopens our isolated fixture.
public static class PersistenceDesktopVerification
{
    public static ImmutableArray<MeasureChange> DashboardMeasures =>
        [.. SyntheticDesktopFixture.Dashboard.Metrics.Select(m => new MeasureChange(m.Table, m.Name, m.Expression)),
         .. new[] { "default", "line", "period" }.Select(DashboardDesktopVerification.CompileFixtureVariant)];

    public static void ValidateCompletedFixture(Database database)
    {
        OwnedDesktopVerification.ValidateBaseline(database, true, true);
        var sales = database.Model.Tables["Sales"];
        foreach (var change in SyntheticDesktopFixture.ObjectChanges.Measures.Concat(DashboardMeasures))
        {
            var measure = sales.Measures.Find(change.Name) ?? throw new InvalidDataException("Persisted fixture measure missing.");
            var observed = TomObjectMutations.ReadMeasure(measure);
            if (observed.Expression != change.Expression || observed.Owner != DefinitionRules.Owner ||
                observed.OperationId is null || !observed.IntegrityValid)
                throw new InvalidDataException($"Persisted fixture measure differs: {change.Name}; expression={observed.Expression == change.Expression}; owner={observed.Owner == DefinitionRules.Owner}; operation={observed.OperationId is not null}; integrity={observed.IntegrityValid}.");
        }
        var column = sales.Columns.Find("SA_FixtureCheck Profit") as CalculatedColumn
            ?? throw new InvalidDataException("Persisted fixture calculated column missing.");
        var actualColumn = TomObjectMutations.ReadColumn(column);
        if (actualColumn.Expression != SyntheticDesktopFixture.ObjectChanges.Columns[0].Expression ||
            column.DataType != DataType.Decimal || actualColumn.Owner != DefinitionRules.Owner ||
            actualColumn.OperationId is null || !actualColumn.IntegrityValid)
            throw new InvalidDataException("Persisted fixture calculated column differs.");
        var relation = database.Model.Relationships.Find("SA_FixtureCheck Review region") as SingleColumnRelationship
            ?? throw new InvalidDataException("Persisted fixture relationship missing.");
        var actualRelation = TomObjectMutations.ReadRelationship(relation);
        if (actualRelation.Definition != SyntheticDesktopFixture.ObjectChanges.Relationships[0] ||
            actualRelation.Owner != DefinitionRules.Owner || actualRelation.OperationId is null || !actualRelation.IntegrityValid)
            throw new InvalidDataException("Persisted fixture relationship differs.");
    }

    public static async Task<int> RunAsync(string reopenedRecordPath, string originalRecordPath)
    {
        var gates = new List<object>();
        var stage = "distinct_owned_restart";
        var evidencePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reopenedRecordPath))!,
            "persistence-verification-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var original = await ReadSession(originalRecordPath);
            var reopened = await ReadSession(reopenedRecordPath);
            OwnedDesktopVerification.EnsureOwned(reopened);
            if (!Path.GetFullPath(original.ProjectPath).Equals(Path.GetFullPath(reopened.ProjectPath), StringComparison.OrdinalIgnoreCase) ||
                (original.DesktopProcessId == reopened.DesktopProcessId && original.DesktopStartedUtcTicks == reopened.DesktopStartedUtcTicks) ||
                (original.EngineProcessId == reopened.EngineProcessId && original.EngineStartedUtcTicks == reopened.EngineStartedUtcTicks) ||
                IsCurrent(original.DesktopProcessId, original.DesktopStartedUtcTicks) || IsCurrent(original.EngineProcessId, original.EngineStartedUtcTicks))
                throw new InvalidDataException("Fixture must be reopened in distinct processes after the original pair exits.");
            var discovery = new DesktopDiscovery(new WindowsLocalPortInventory());
            var instance = discovery.Discover([Path.GetDirectoryName(reopened.Workspace)!]).Single(i =>
                i.Workspace.Equals(Path.GetFullPath(reopened.Workspace), StringComparison.OrdinalIgnoreCase) &&
                i.Owner.ProcessId == reopened.EngineProcessId && i.Owner.StartedUtcTicks == reopened.EngineStartedUtcTicks);
            void Ensure() { OwnedDesktopVerification.EnsureOwned(reopened); discovery.EnsureCurrent(instance); }
            void Pass(string gate) { Ensure(); gates.Add(new { Gate = gate, Pass = true }); }
            Pass(stage);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = timeout.Token;
            stage = "persisted_definitions_and_ownership";
            using var server = new Server();
            server.Connect("Data Source=localhost:" + instance.Owner.Port + ";Connect Timeout=3");
            if (server.Databases.Count != 1) throw new InvalidDataException("Reopened fixture catalog differs.");
            var database = server.Databases[0];
            ValidateCompletedFixture(database); Pass(stage);
            var adapter = new DesktopModelAdapter(instance, database.ID, discovery);
            var snapshot = await adapter.ReadSnapshotAsync(token);
            foreach (var query in SyntheticDesktopFixture.Queries.Concat(new[] {
                new FixtureQuery("written_values", "EVALUATE ROW(\"WrittenRevenue\", [SA_FixtureCheck Revenue], \"WrittenProfit\", SUM('Sales'[SA_FixtureCheck Profit]))",
                    ContractJson.Element(new[] { new { WrittenRevenue = 700m, WrittenProfit = 280m } })),
                new FixtureQuery("written_relationship", "EVALUATE SUMMARIZECOLUMNS('Review regions'[Region], \"Revenue\", [Revenue]) ORDER BY 'Review regions'[Region]",
                    ContractJson.Element(new[] { new { Region = "North", Revenue = 380m }, new { Region = "South", Revenue = 320m } })) }))
            {
                stage = "persisted_dax_" + query.Id; Ensure();
                OwnedDesktopVerification.ValidateResult(query, await adapter.ExecuteAsync(snapshot.Identity, query.Dax, new(), token));
                Pass(stage);
            }
            foreach (var variant in new[] { "default", "line", "period" })
            foreach (var filter in new[] { "All", "North", "South", "Missing" })
            {
                stage = "persisted_html_" + variant + "_" + filter; Ensure();
                var measure = DashboardPlans.Field(new("Sales", "SA_Fixture HTML " + variant));
                var value = filter == "All" ? measure : "CALCULATE(" + measure + ", TREATAS({\"" + filter + "\"}, 'Regions'[Region]))";
                var result = await adapter.ExecuteAsync(snapshot.Identity, "EVALUATE ROW(\"Html\", " + value + ")",
                    new(1, 64_000, TimeSpan.FromSeconds(30)), token);
                if (result.Truncated || result.Rows.Length != 1 || result.Rows[0].Length != 1 || result.Rows[0][0].ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Persisted HTML result differs.");
                var expected = filter switch { "All" => DashboardDesktopVerification.AllRows, "North" => DashboardDesktopVerification.NorthRows,
                    "South" => DashboardDesktopVerification.SouthRows, _ => [] };
                if (variant == "period") expected = expected.Take(1).ToImmutableArray();
                DashboardDesktopVerification.ValidateDynamicHtml(result.Rows[0][0].GetString()!, expected, variant == "line");
                Pass(stage);
            }
            await Save(true); Console.WriteLine(evidencePath); return 0;
        }
        catch (Exception error)
        {
            gates.Add(new { Gate = stage, Pass = false, ErrorType = error.GetType().Name,
                Reason = error is InvalidDataException ? error.Message : "Owned fixture unavailable; raw server errors omitted." });
            await Save(false); Console.Error.WriteLine(evidencePath); return 1;
        }
        async Task Save(bool passed)
        {
            using var file = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, new { Version = 1, CapturedUtc = DateTimeOffset.UtcNow, Synthetic = true,
                Passed = passed, ReadOnly = true, SavedReopenedVerified = passed, PrivateModelsAccessed = false,
                RealAiProviderVerified = false, CloudVerified = false, NativeAssistantUiVerified = false,
                HtmlCustomVisualVerified = false, Gates = gates }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static async Task<OwnedDesktopVerification.OwnedSession> ReadSession(string path)
    {
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Owned session size differs.");
        return JsonSerializer.Deserialize<OwnedDesktopVerification.OwnedSession>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("Owned session missing.");
    }
    private static bool IsCurrent(int pid, long ticks)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
    }
}
