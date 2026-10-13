using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

// Explicit acceptance tool for a newly launched, self-authored fixture. Never discovers user catalogs.
public static class OwnedDesktopVerification
{
    public sealed record OwnedSession(string ProjectPath, string DesktopExe, int DesktopProcessId,
        long DesktopStartedUtcTicks, string DesktopVersion, int EngineProcessId, long EngineStartedUtcTicks, string Workspace);

    public static void ValidateBaseline(Database database, bool allowWrittenObjects = false)
    {
        using var expected = SyntheticDesktopFixture.CreateDatabase();
        var model = database.Model;
        if (model.Annotations.Find(SyntheticDesktopFixture.MarkerName)?.Value != SyntheticDesktopFixture.MarkerValue ||
            model.Tables.Count != expected.Model.Tables.Count || model.DataSources.Count != 0 ||
            model.Expressions.Count != 0 || model.Roles.Count != 0 ||
            model.Relationships.Count != 2 + (allowWrittenObjects && model.Relationships.ContainsName("SA_FixtureCheck Review region") ? 1 : 0))
            throw new InvalidDataException($"Fixture baseline identity differs: marker={model.Annotations.Find(SyntheticDesktopFixture.MarkerName)?.Value == SyntheticDesktopFixture.MarkerValue}, tables={model.Tables.Count}, sources={model.DataSources.Count}, expressions={model.Expressions.Count}, roles={model.Roles.Count}, relationships={model.Relationships.Count}.");
        foreach (var table in expected.Model.Tables)
        {
            var actual = model.Tables.Find(table.Name) ?? throw new InvalidDataException("Fixture table differs.");
            var userColumns = actual.Columns.Where(c => c is not RowNumberColumn && !(allowWrittenObjects && table.Name == "Sales" && c is CalculatedColumn && c.Name == "SA_FixtureCheck Profit")).ToArray();
            if (userColumns.Length != table.Columns.Count || actual.Columns.OfType<RowNumberColumn>().Count() > 1 ||
                actual.Measures.Count != table.Measures.Count + (allowWrittenObjects && table.Name == "Sales" && actual.Measures.ContainsName("SA_FixtureCheck Revenue") ? 1 : 0) || actual.Partitions.Count != 1)
                throw new InvalidDataException($"Fixture shape differs: {table.Name}, columns={actual.Columns.Count}, measures={actual.Measures.Count}, partitions={actual.Partitions.Count}.");
            if (actual.Partitions[0].Source is not MPartitionSource source ||
                Canonical(source.Expression) != Canonical(((MPartitionSource)table.Partitions[0].Source).Expression))
                throw new InvalidDataException("Fixture inline source differs: " + table.Name + ".");
            foreach (var column in table.Columns.Cast<DataColumn>())
                if (actual.Columns.Find(column.Name) is not DataColumn found || found.SourceColumn != column.SourceColumn || found.DataType != column.DataType)
                    throw new InvalidDataException("Fixture source column differs.");
            foreach (var measure in table.Measures)
                if (actual.Measures.Find(measure.Name)?.Expression != measure.Expression)
                    throw new InvalidDataException("Fixture measure differs.");
        }
        foreach (var relation in expected.Model.Relationships.Cast<SingleColumnRelationship>())
            if (model.Relationships.Find(relation.Name) is not SingleColumnRelationship found ||
                found.FromColumn.Table.Name != relation.FromColumn.Table.Name || found.FromColumn.Name != relation.FromColumn.Name ||
                found.ToColumn.Table.Name != relation.ToColumn.Table.Name || found.ToColumn.Name != relation.ToColumn.Name ||
                found.IsActive != relation.IsActive || found.FromCardinality != relation.FromCardinality ||
                found.ToCardinality != relation.ToCardinality || found.CrossFilteringBehavior != relation.CrossFilteringBehavior)
                throw new InvalidDataException("Fixture relationship differs.");
    }

    public static async Task<int> InspectAsync(string recordPath)
    {
        try
        {
            var owned = JsonSerializer.Deserialize<OwnedSession>(await File.ReadAllTextAsync(recordPath)) ?? throw new InvalidDataException();
            EnsureOwned(owned);
            var discovery = new DesktopDiscovery(new WindowsLocalPortInventory());
            var instance = discovery.Discover([Path.GetDirectoryName(owned.Workspace)!]).Single(i =>
                i.Workspace.Equals(Path.GetFullPath(owned.Workspace), StringComparison.OrdinalIgnoreCase) && i.Owner.ProcessId == owned.EngineProcessId && i.Owner.StartedUtcTicks == owned.EngineStartedUtcTicks);
            using var server = new Server(); server.Connect("Data Source=localhost:" + instance.Owner.Port + ";Connect Timeout=3");
            if (server.Databases.Count != 1) throw new InvalidDataException();
            var database = server.Databases[0]; ValidateBaseline(database, true); EnsureOwned(owned); discovery.EnsureCurrent(instance);
            var sales = database.Model.Tables["Sales"];
            var measure = sales.Measures.Find("SA_FixtureCheck Revenue");
            var column = sales.Columns.Find("SA_FixtureCheck Profit") as CalculatedColumn;
            var relation = database.Model.Relationships.Find("SA_FixtureCheck Review region") as SingleColumnRelationship;
            var result = new { ReadOnly = true, Synthetic = true,
                Measure = measure is null ? null : new { Definition = TomObjectMutations.ReadMeasure(measure), Json = Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeObject(measure) },
                Column = column is null ? null : new { Definition = TomObjectMutations.ReadColumn(column), Json = Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeObject(column) },
                Relationship = relation is null ? null : new { Definition = TomObjectMutations.ReadRelationship(relation), Json = Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeObject(relation) } };
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, "owned-object-inspection-" + Guid.NewGuid().ToString("N") + ".json");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(path); return 0;
        }
        catch { Console.Error.WriteLine("Owned fixture inspection denied or unavailable."); return 1; }
    }

    public static void ValidateResult(FixtureQuery expected, QueryResult result)
    {
        var rows = expected.ExpectedRows.EnumerateArray().ToArray();
        if (result.Truncated || result.Rows.Length != rows.Length) throw new InvalidDataException("Fixture row count differs.");
        for (var i = 0; i < rows.Length; i++)
        {
            var fields = rows[i].EnumerateObject().ToArray();
            if (fields.Length != result.Columns.Length) throw new InvalidDataException("Fixture result width differs.");
            foreach (var field in fields)
            {
                var name = field.Name == "MonthNumber" ? "Month number" : field.Name;
                var candidates = result.Columns.Select((column, index) => (column, index)).Where(c =>
                    c.column == name || c.column == "[" + name + "]" ||
                    c.column == "Regions[" + name + "]" || c.column == "Dates[" + name + "]" ||
                    c.column == "Review regions[" + name + "]").ToArray();
                if (candidates.Length != 1) throw new InvalidDataException("Fixture result field differs.");
                var value = result.Rows[i][candidates[0].index];
                if (field.Value.ValueKind == JsonValueKind.Number)
                {
                    if (value.ValueKind != JsonValueKind.Number || Math.Abs(value.GetDecimal() - field.Value.GetDecimal()) > 0.0000001m)
                        throw new InvalidDataException("Fixture numeric result differs.");
                }
                else if (field.Value.ToString() != value.ToString()) throw new InvalidDataException("Fixture text result differs.");
            }
        }
    }

    public static async Task<int> RunAsync(string recordPath)
    {
        var gates = new List<object>();
        var stage = "owned_process";
        var evidencePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, "live-verification-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (new FileInfo(recordPath).Length > 16_384) throw new InvalidDataException();
            var owned = JsonSerializer.Deserialize<OwnedSession>(await File.ReadAllTextAsync(recordPath)) ?? throw new InvalidDataException();
            EnsureOwned(owned);
            var inventory = new WindowsLocalPortInventory();
            var discovery = new DesktopDiscovery(inventory);
            var instance = discovery.Discover([Path.GetDirectoryName(owned.Workspace)!]).Single(i =>
                i.Workspace.Equals(Path.GetFullPath(owned.Workspace), StringComparison.OrdinalIgnoreCase) &&
                i.Owner.ProcessId == owned.EngineProcessId && i.Owner.StartedUtcTicks == owned.EngineStartedUtcTicks);
            void Ensure() { EnsureOwned(owned); discovery.EnsureCurrent(instance); }
            Ensure(); gates.Add(new { Gate = stage, Pass = true, DesktopPid = owned.DesktopProcessId, EnginePid = owned.EngineProcessId, Port = instance.Owner.Port });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var token = timeout.Token;
            stage = "fixture_baseline";
            using var server = new Server();
            server.Connect("Data Source=localhost:" + instance.Owner.Port + ";Connect Timeout=3");
            for (var attempt = 0; server.Databases.Count == 0 && attempt < 30; attempt++)
            { Ensure(); await Task.Delay(1000, token); server.Disconnect(); server.Connect("Data Source=localhost:" + instance.Owner.Port + ";Connect Timeout=3"); }
            if (server.Databases.Count != 1) throw new InvalidDataException("Owned fixture catalog count differs.");
            var database = server.Databases[0];
            ValidateBaseline(database); Ensure();
            gates.Add(new { Gate = stage, Pass = true, Tables = 4, Measures = 5, Relationships = 2 });
            Console.WriteLine("fixture-baseline=verified");
            stage = "inline_refresh";
            database.Model.RequestRefresh(RefreshType.Full); database.Model.SaveChanges();
            Ensure(); gates.Add(new { Gate = stage, Pass = true });
            Console.WriteLine("fixture-inline-refresh=verified");
            var adapter = new DesktopModelAdapter(instance, database.ID, discovery);
            stage = "metadata_adapter";
            var snapshot = await adapter.ReadSnapshotAsync(token);
            if (snapshot.Tables.Length != 4 || snapshot.Tables.Sum(t => t.Columns.Length) != 13 || snapshot.Relationships.Length != 2)
                throw new InvalidDataException();
            gates.Add(new { Gate = stage, Pass = true });
            foreach (var query in SyntheticDesktopFixture.Queries)
            {
                stage = "dax_" + query.Id; Ensure();
                var result = await adapter.ExecuteAsync(snapshot.Identity, query.Dax, new(), token);
                ValidateResult(query, result); gates.Add(new { Gate = stage, Pass = true, Rows = result.Rows.Length });
                Console.WriteLine(stage + "=verified");
            }
            stage = "writer_preview_without_approval";
            var writer = adapter.CreateWriter();
            var session = new PermissionSession(snapshot.Identity, ConversationMode.Analyze,
                ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
            var coordinator = new ModelWriteCoordinator(writer, session);
            var preview = await coordinator.PrepareAsync(SyntheticDesktopFixture.ObjectChanges, session.Snapshot, token);
            var denied = await coordinator.ApplyAsync(preview, null, session.Snapshot, token);
            if (denied.Code != "approval_denied" || (await writer.ReadDefinitionAsync(token)).Revision != preview.Before.Revision)
                throw new InvalidDataException("Unapproved preview changed fixture.");
            gates.Add(new { Gate = stage, Pass = true });
            stage = "writer_apply_readback"; Ensure();
            // The user authorized verification; this consent applies only to the fixed, proven synthetic fixture plan.
            var approval = coordinator.ApproveAfterUserConfirmation(preview, session.Snapshot);
            var outcome = await coordinator.ApplyAsync(preview, approval, session.Snapshot, token);
            if (!outcome.Success) throw new InvalidDataException("Fixture writer outcome: " + outcome.Code);
            gates.Add(new { Gate = stage, Pass = true, OperationId = preview.Plan.OperationId, outcome.Code });
            Console.WriteLine("fixture-writer-readback=verified");
            stage = "writer_idempotent_receipt";
            var receipt = await coordinator.ApplyAsync(preview, approval, session.Snapshot, token);
            if (receipt.Code != "already_applied") throw new InvalidDataException("Fixture receipt differs.");
            gates.Add(new { Gate = stage, Pass = true });
            snapshot = await adapter.ReadSnapshotAsync(token);
            foreach (var query in new[] {
                new FixtureQuery("written_values", "EVALUATE ROW(\"WrittenRevenue\", [SA_FixtureCheck Revenue], \"WrittenProfit\", SUM('Sales'[SA_FixtureCheck Profit]))",
                    ContractJson.Element(new[] { new { WrittenRevenue = 700m, WrittenProfit = 280m } })),
                new FixtureQuery("written_relationship", "EVALUATE SUMMARIZECOLUMNS('Review regions'[Region], \"Revenue\", [Revenue]) ORDER BY 'Review regions'[Region]",
                    ContractJson.Element(new[] { new { Region = "North", Revenue = 380m }, new { Region = "South", Revenue = 320m } })) })
            {
                stage = "dax_" + query.Id; Ensure();
                var result = await adapter.ExecuteAsync(snapshot.Identity, query.Dax, new(), token);
                ValidateResult(query, result); gates.Add(new { Gate = stage, Pass = true, Rows = result.Rows.Length });
            }
            await DashboardDesktopVerification.RunAsync(adapter, Path.GetDirectoryName(Path.GetFullPath(recordPath))!,
                Ensure, value => stage = value, gates.Add, token);
            await Save(true, null); Console.WriteLine(evidencePath); return 0;
        }
        catch (Exception error)
        {
            gates.Add(new { Gate = stage, Pass = false, ErrorType = error.GetType().Name,
                ErrorChain = ErrorCodes(error), Reason = error is InvalidDataException ? error.Message : "Runtime operation failed; no raw server error retained." });
            await Save(false, stage); Console.Error.WriteLine(evidencePath); return 1;
        }
        async Task Save(bool passed, string? failedStage)
        {
            using var file = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, new { Version = 1, CapturedUtc = DateTimeOffset.UtcNow, Synthetic = true,
                Passed = passed, FailedStage = failedStage, Gates = gates, RealAiProviderVerified = false, CloudVerified = false,
                HtmlVisualVerified = false, NativeAssistantUiVerified = false,
                DashboardLiveEngineVerified = passed, DynamicHtmlFilterContextVerified = passed }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static object[] ErrorCodes(Exception exception)
    {
        var codes = new List<object>();
        for (var current = exception; current is not null && codes.Count < 8; current = current.InnerException)
            codes.Add(new { Type = current.GetType().Name, HResult = current.HResult.ToString("X8") });
        return codes.ToArray();
    }

    // TMDL may normalize indentation; this fixed fixture has no multi-line text literals.
    private static string Canonical(string value) => string.Join("\n", value.Replace("\r\n", "\n").Trim().Split('\n').Select(line => line.Trim()));
    private static void EnsureOwned(OwnedSession owned)
    {
        if (!OperatingSystem.IsWindows() || Path.GetFileName(owned.ProjectPath) != SyntheticDesktopFixture.ProjectName + ".pbip" ||
            !File.Exists(owned.ProjectPath)) throw new InvalidDataException("Owned fixture project missing.");
        using var desktop = Process.GetProcessById(owned.DesktopProcessId);
        using var engine = Process.GetProcessById(owned.EngineProcessId);
        var expectedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Power BI Desktop", "bin", "PBIDesktop.exe");
        if (!Path.GetFullPath(owned.DesktopExe).Equals(expectedExe, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(desktop.MainModule?.FileName, expectedExe, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(engine.MainModule?.FileName, Path.Combine(Path.GetDirectoryName(expectedExe)!, "msmdsrv.exe"), StringComparison.OrdinalIgnoreCase) ||
            desktop.StartTime.ToUniversalTime().Ticks != owned.DesktopStartedUtcTicks || engine.StartTime.ToUniversalTime().Ticks != owned.EngineStartedUtcTicks ||
            NtQueryInformationProcess(engine.Handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 ||
            info.ParentPid.ToInt64() != desktop.Id) throw new InvalidDataException("Owned process lineage differs.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    { public IntPtr Reserved1, Peb, Reserved2A, Reserved2B, Pid, ParentPid; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, out ProcessBasicInformation info, int length, out int returned);
}
