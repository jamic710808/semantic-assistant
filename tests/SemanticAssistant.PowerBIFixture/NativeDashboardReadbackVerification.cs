using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

// Fixed read-only engine checks after separately observed native Dashboard actions.
public static class NativeDashboardReadbackVerification
{
    public static void ValidateDefinitions(Database database)
    {
        NativeWriterReadbackVerification.ValidateDefinitions(database, allowDashboardObjects: true);
        var sales = database.Model.Tables["Sales"];
        if (sales.Measures.Count != 10) throw new InvalidDataException("Native Dashboard fixed shape differs.");
        Guid? operation = null;
        foreach (var change in SyntheticDesktopFixture.Dashboard.Metrics)
        {
            var observed = TomObjectMutations.ReadMeasure(sales.Measures[change.Name]);
            if (observed.Expression != change.Expression || observed.Owner != DefinitionRules.Owner ||
                !observed.IntegrityValid || observed.OperationId is null || (operation is not null && operation != observed.OperationId))
                throw new InvalidDataException("Native Dashboard metric differs.");
            operation = observed.OperationId;
        }
        var html = DashboardDesktopVerification.CompileFixtureVariant("default");
        var actual = TomObjectMutations.ReadMeasure(sales.Measures[html.Name]);
        if (actual.Expression != html.Expression || actual.Owner != DefinitionRules.Owner || !actual.IntegrityValid ||
            actual.OperationId is null || actual.OperationId == operation || sales.Measures.ContainsName("SA_DashboardHTML"))
            throw new InvalidDataException("Native Dashboard independent HTML operation differs.");
    }

    public static async Task<int> RunAsync(string recordPath)
    {
        var gates = new List<object>();
        var stage = "owned_process";
        var evidence = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, "native-dashboard-readback-" + Guid.NewGuid().ToString("N") + ".json");
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
            stage = "fixed_definitions_and_independent_html_operation";
            var database = server.Databases[0]; ValidateDefinitions(database); Pass();
            var adapter = new DesktopModelAdapter(instance, database.ID, discovery);
            var snapshot = await adapter.ReadSnapshotAsync(timeout.Token);
            stage = "dashboard_monthly_values";
            var dax = "EVALUATE SUMMARIZECOLUMNS('Dates'[Month], \"SA_Fixture Revenue\", [SA_Fixture Revenue], \"SA_Fixture Cost\", [SA_Fixture Cost], \"SA_Fixture Profit\", [SA_Fixture Profit]) ORDER BY 'Dates'[Month]";
            OwnedDesktopVerification.ValidateResult(DashboardDesktopVerification.ExpectedQuery(DashboardDesktopVerification.AllRows, dax),
                await adapter.ExecuteAsync(snapshot.Identity, dax, new(), timeout.Token)); Pass();
            foreach (var filter in new[] { "All", "North", "South", "Missing" })
            {
                stage = "dynamic_html_" + filter; Ensure();
                var value = filter == "All" ? "[SA_Fixture HTML default]" : "CALCULATE([SA_Fixture HTML default], TREATAS({\"" + filter + "\"}, 'Regions'[Region]))";
                var result = await adapter.ExecuteAsync(snapshot.Identity, "EVALUATE ROW(\"Html\", " + value + ")", new(1, 64_000, TimeSpan.FromSeconds(30)), timeout.Token);
                if (result.Truncated || result.Rows.Length != 1 || result.Rows[0].Length != 1 || result.Rows[0][0].ValueKind != JsonValueKind.String)
                    throw new InvalidDataException();
                var expected = filter switch { "All" => DashboardDesktopVerification.AllRows, "North" => DashboardDesktopVerification.NorthRows,
                    "South" => DashboardDesktopVerification.SouthRows, _ => [] };
                DashboardDesktopVerification.ValidateDynamicHtml(result.Rows[0][0].GetString()!, expected); Pass();
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
                Passed = passed, ReadOnly = true, ActualEngineDashboardVerified = passed, NativeAssistantUiVerified = false,
                PrivateModelsAccessed = false, RealAiProviderVerified = false, CloudVerified = false, HtmlCustomVisualVerified = false,
                Gates = gates }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
