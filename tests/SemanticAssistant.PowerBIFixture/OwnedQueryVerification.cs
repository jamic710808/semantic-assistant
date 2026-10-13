using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

// Small fixed queries against only our proven inline fixture; no writes or refresh.
public static class OwnedQueryVerification
{
    public static async Task<bool> WasRejectedAsync<T>(Func<Task<QueryResult>> query) where T:Exception
    {
        try { await query(); return false; }
        catch(T) { return true; }
    }
    public static void ValidateFixture(Database database)
    {
        if (database.Model.Tables.Find("Sales")?.Measures.Count == 5)
            OwnedDesktopVerification.ValidateBaseline(database);
        else NativeDashboardReadbackVerification.ValidateDefinitions(database);
    }

    public static async Task<int> RunAsync(string recordPath)
    {
        var stage = "owned_process";
        var gates = new List<object>();
        var evidence = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!, "owned-query-verification-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (new FileInfo(recordPath).Length > 16_384) throw new InvalidDataException();
            var owned = JsonSerializer.Deserialize<OwnedDesktopVerification.OwnedSession>(await File.ReadAllTextAsync(recordPath)) ?? throw new InvalidDataException();
            void Owned() => OwnedDesktopVerification.EnsureOwned(owned);
            Owned();
            var pinned = new PinnedFixturePortInventory(new WindowsLocalPortInventory(), owned.EngineProcessId, owned.EngineStartedUtcTicks, Owned);
            var probe = new ProbeInventory(pinned);
            var discovery = new DesktopDiscovery(probe);
            var instance = discovery.Discover([Path.GetDirectoryName(owned.Workspace)!]).Single(i => i.Workspace.Equals(Path.GetFullPath(owned.Workspace), StringComparison.OrdinalIgnoreCase));
            void Ensure() { Owned(); discovery.EnsureCurrent(instance); }
            void Pass(object? details = null) { Ensure(); gates.Add(new { Gate=stage, Pass=true, Details=details }); }
            Pass();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = new Server();
            server.Connect("Data Source=localhost:" + instance.Owner.Port + ";Connect Timeout=3");
            if (server.Databases.Count != 1) throw new InvalidDataException();
            stage = "fixed_inline_fixture"; ValidateFixture(server.Databases[0]); Pass();
            var adapter = new DesktopModelAdapter(instance, server.Databases[0].ID, discovery);
            var snapshot = await adapter.ReadSnapshotAsync(timeout.Token);
            const string days = "EVALUATE SELECTCOLUMNS(TOPN(5, 'Dates', 'Dates'[Date], ASC), \"Day\", DAY('Dates'[Date])) ORDER BY [Day]";

            stage = "row_limit_truncates";
            var limited = await adapter.ExecuteAsync(snapshot.Identity, days, new(2), timeout.Token);
            if (!limited.Truncated || limited.Rows.Length != 2 || limited.Columns.Length != 1 ||
                limited.Rows[0][0].GetInt32()!=1 || limited.Rows[1][0].GetInt32()!=2) throw new InvalidDataException();
            Pass(new { Rows=limited.Rows.Length, Bytes=Bytes(limited), limited.Truncated });

            stage = "exact_row_limit_is_complete";
            var exact = await adapter.ExecuteAsync(snapshot.Identity, days, new(5), timeout.Token);
            if (exact.Truncated || exact.Rows.Length!=5 || exact.Rows.Select((r,i)=>r[0].GetInt32()==i+1).Any(ok=>!ok)) throw new InvalidDataException();
            Pass(new { Rows=exact.Rows.Length, Bytes=Bytes(exact), exact.Truncated });

            stage = "complete_utf8_envelope_limit";
            var wide = await adapter.ExecuteAsync(snapshot.Identity,
                "EVALUATE SELECTCOLUMNS(TOPN(5, 'Dates', 'Dates'[Date], ASC), \"Text\", REPT(\"Z\",128))", new(MaxBytes:1024), timeout.Token);
            if (!wide.Truncated || wide.Rows.Length>=5 || Bytes(wide)>1024 || wide.Rows.Any(r=>r[0].GetString()!=new string('Z',128))) throw new InvalidDataException();
            Pass(new { Rows=wide.Rows.Length, Bytes=Bytes(wide), wide.Truncated });

            stage = "escaped_unicode_cell_limit";
            var escaped = await adapter.ExecuteAsync(snapshot.Identity, "EVALUATE ROW(\"Text\",REPT(\"合\",200))", new(MaxBytes:1024), timeout.Token);
            if (!escaped.Truncated || !escaped.Rows.IsEmpty || Bytes(escaped)>1024) throw new InvalidDataException();
            Pass(new { Rows=0, Bytes=Bytes(escaped), escaped.Truncated });

            await Denied<InvalidDataException>("oversize_single_cell_denied", snapshot.Identity, "EVALUATE ROW(\"Text\",REPT(\"Z\",1025))", new(MaxBytes:1024));
            await Denied<InvalidDataException>("oversize_header_denied", snapshot.Identity, "EVALUATE ROW(\"" + new string('H',1500) + "\",1)", new(MaxBytes:1024));
            await Denied<InvalidDataException>("empty_result_large_query_denied", snapshot.Identity,
                "EVALUATE FILTER(ROW(\"X\",1),FALSE()) /*" + new string('p',1500) + "*/", new(MaxBytes:1024));
            await Denied<IOException>("stale_revision_denied", snapshot.Identity with { Revision="not-current" }, days, new());

            stage = "pre_cancelled_no_discovery";
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); probe.Reset();
                try { await adapter.ExecuteAsync(snapshot.Identity, days, new(), cancel.Token); throw new InvalidDataException(); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                if (probe.Reads!=0) throw new InvalidDataException();
                Pass(new { AdapterInventoryReads=0, CommandSubmitted=false });
            }

            stage = "cancel_after_connection_guard";
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                // ReadSnapshot has two EnsureCurrent checks (4 reads); connection.Open
                // is followed by the third check (reads 5 and 6), before ExecuteReader.
                probe.Reset(n=> { if(n==6) cancel.Cancel(); });
                try { await adapter.ExecuteAsync(snapshot.Identity, days, new(), cancel.Token); throw new InvalidDataException(); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                var reads=probe.Reads; probe.Reset();
                if(reads!=6) throw new InvalidDataException();
                Pass(new { AdapterInventoryReads=reads, CommandSubmitted=false, GuardTriggeredCancellation=true });
            }

            stage = "adapter_deadline_during_metadata";
            probe.Reset(n=> { if(n==1) Thread.Sleep(150); });
            var watch=Stopwatch.StartNew();
            try { await adapter.ExecuteAsync(snapshot.Identity, days, new(Timeout:TimeSpan.FromMilliseconds(100)), timeout.Token); throw new InvalidDataException(); }
            catch(OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            var deadlineReads=probe.Reads; probe.Reset();
            if(watch.Elapsed>TimeSpan.FromSeconds(8) || deadlineReads==0) throw new InvalidDataException();
            Pass(new { DeadlineMilliseconds=100, ElapsedMilliseconds=watch.Elapsed.TotalMilliseconds, InjectedGuardDelayMilliseconds=150, CommandSubmitted=false });

            stage = "simulated_pin_loss_before_connection";
            probe.Reset(n=> { if(n==1) throw new IOException(); });
            try { await adapter.ExecuteAsync(snapshot.Identity, days, new(), timeout.Token); throw new InvalidDataException(); }
            catch(IOException) { }
            var lostReads=probe.Reads; probe.Reset();
            if(lostReads!=1) throw new InvalidDataException();
            Pass(new { Injected=true, AdapterInventoryReads=1, ActualProcessRace=false });

            stage = "fresh_query_recovers_after_denials";
            var recovered=await adapter.ExecuteAsync(snapshot.Identity, "EVALUATE ROW(\"Revenue\",SUM('Sales'[Revenue amount]))", new(), timeout.Token);
            if(recovered.Truncated || recovered.Rows.Length!=1 || recovered.Rows[0][0].GetDecimal()!=700m) throw new InvalidDataException();
            Pass(new { Rows=1 });
            stage = "fixture_unchanged";
            if((await adapter.ReadSnapshotAsync(timeout.Token)).Identity!=snapshot.Identity) throw new InvalidDataException();
            Pass(); await Save(true); Console.WriteLine(evidence); return 0;

            async Task Denied<T>(string name, ModelIdentity target, string dax, QueryLimits limits) where T:Exception
            {
                stage=name;
                if(!await WasRejectedAsync<T>(()=>adapter.ExecuteAsync(target,dax,limits,timeout.Token)))
                    throw new InvalidDataException("Expected rejection missing.");
                Pass(new { ErrorType=typeof(T).Name, ResultPublished=false });
            }
        }
        catch(Exception error)
        {
            gates.Add(new { Gate=stage, Pass=false, ErrorType=error.GetType().Name });
            await Save(false); Console.Error.WriteLine(evidence); return 1;
        }
        async Task Save(bool passed)
        {
            using var file=new FileStream(evidence,FileMode.CreateNew,FileAccess.Write);
            await JsonSerializer.SerializeAsync(file,new { Version=1,CapturedUtc=DateTimeOffset.UtcNow,Synthetic=true,ReadOnly=true,Passed=passed,
                ActualEngineResultLimitsVerified=passed,InFlightQueryCancellationVerified=false,ActualProcessRaceVerified=false,
                NativeAssistantUiVerified=false,PrivateModelsAccessed=false,RealAiProviderVerified=false,CloudVerified=false,Gates=gates },new JsonSerializerOptions { WriteIndented=true });
        }
    }

    private static int Bytes(QueryResult result)=>JsonSerializer.SerializeToUtf8Bytes(result).Length;
    private sealed class ProbeInventory(ILocalPortInventory inner):ILocalPortInventory
    {
        public int Reads { get; private set; }
        private Action<int>? probe;
        public void Reset(Action<int>? callback=null) { Reads=0;probe=callback; }
        public ImmutableArray<LocalPortOwner> ReadOwners() { Reads++; probe?.Invoke(Reads); return inner.ReadOwners(); }
    }
}
