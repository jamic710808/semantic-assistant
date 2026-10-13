using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

// Read-only engine evidence. UI observations are a separate acceptance artifact.
public static class NativeWriterReadbackVerification
{
    public static void ValidateDefinitions(Database database, bool allowDashboardObjects = false)
    {
        OwnedDesktopVerification.ValidateBaseline(database, allowWrittenObjects: true, allowDashboardObjects: allowDashboardObjects);
        var changes = SyntheticDesktopFixture.ObjectChanges;
        var sales = database.Model.Tables["Sales"];
        var measure = TomObjectMutations.ReadMeasure(sales.Measures[changes.Measures[0].Name]);
        var column = TomObjectMutations.ReadColumn((CalculatedColumn)sales.Columns[changes.Columns[0].Name]);
        var relation = TomObjectMutations.ReadRelationship((SingleColumnRelationship)database.Model.Relationships[changes.Relationships[0].Name]);
        if (measure.Expression != changes.Measures[0].Expression || column.Expression != changes.Columns[0].Expression ||
            column.DataType != changes.Columns[0].DataType.ToString() || relation.Definition != changes.Relationships[0] ||
            measure.Owner != DefinitionRules.Owner || column.Owner != DefinitionRules.Owner || relation.Owner != DefinitionRules.Owner ||
            !measure.IntegrityValid || !column.IntegrityValid || !relation.IntegrityValid ||
            measure.OperationId is null || column.OperationId != measure.OperationId || relation.OperationId != measure.OperationId)
            throw new InvalidDataException("Fixed native writer definitions or ownership differ.");
    }

    public static async Task<int> RunAsync(string recordPath)
    {
        var gates = new List<object>();
        var stage = "owned_process";
        var evidence = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, "native-writer-readback-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (new FileInfo(recordPath).Length > 16_384) throw new InvalidDataException();
            var owned = JsonSerializer.Deserialize<OwnedDesktopVerification.OwnedSession>(await File.ReadAllTextAsync(recordPath)) ?? throw new InvalidDataException();
            void Owned() => OwnedDesktopVerification.EnsureOwned(owned);
            Owned();
            var discovery = new DesktopDiscovery(new PinnedFixturePortInventory(new WindowsLocalPortInventory(), owned.EngineProcessId, owned.EngineStartedUtcTicks, Owned));
            var instance = discovery.Discover([Path.GetDirectoryName(owned.Workspace)!]).Single(i => i.Workspace.Equals(Path.GetFullPath(owned.Workspace), StringComparison.OrdinalIgnoreCase));
            void Ensure() { Owned(); discovery.EnsureCurrent(instance); }
            void Pass() { Ensure(); gates.Add(new { Gate = stage, Pass = true }); }
            Pass();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var server = new Server();
            server.Connect("Data Source=localhost:" + instance.Owner.Port + ";Connect Timeout=3");
            if (server.Databases.Count != 1) throw new InvalidDataException();
            stage = "fixed_definitions_and_shared_operation";
            var database = server.Databases[0]; ValidateDefinitions(database); Pass();
            var adapter = new DesktopModelAdapter(instance, database.ID, discovery);
            var snapshot = await adapter.ReadSnapshotAsync(timeout.Token);
            foreach (var query in new[] {
                new FixtureQuery("written_values", "EVALUATE ROW(\"WrittenRevenue\", [SA_FixtureCheck Revenue], \"WrittenProfit\", SUM('Sales'[SA_FixtureCheck Profit]))",
                    ContractJson.Element(new[] { new { WrittenRevenue = 700m, WrittenProfit = 280m } })),
                new FixtureQuery("written_relationship", "EVALUATE SUMMARIZECOLUMNS('Review regions'[Region], \"Revenue\", [Revenue]) ORDER BY 'Review regions'[Region]",
                    ContractJson.Element(new[] { new { Region = "North", Revenue = 380m }, new { Region = "South", Revenue = 320m } })) })
            {
                stage = "dax_" + query.Id; Ensure();
                OwnedDesktopVerification.ValidateResult(query, await adapter.ExecuteAsync(snapshot.Identity, query.Dax, new(), timeout.Token)); Pass();
            }
            await Save(true); Console.WriteLine(evidence); return 0;
        }
        catch (Exception error)
        {
            gates.Add(new { Gate = stage, Pass = false, ErrorType = error.GetType().Name });
            await Save(false); Console.Error.WriteLine(evidence); return 1;
        }
        async Task Save(bool passed)
        {
            using var file = new FileStream(evidence, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, new { Version = 1, CapturedUtc = DateTimeOffset.UtcNow, Synthetic = true,
                Passed = passed, ReadOnly = true, ActualEngineWrittenObjectsVerified = passed, NativeAssistantUiVerified = false,
                PrivateModelsAccessed = false, RealAiProviderVerified = false, CloudVerified = false, Gates = gates }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
