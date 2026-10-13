using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.PowerBIFixture;

public static class OwnedCancellationVerification
{
    private sealed class ProbeFailure(string code,int? measuredCharacters=null,int? measuredBytes=null):Exception
    { public string Code {get;}=code;public int? MeasuredCharacters{get;}=measuredCharacters;public int? MeasuredBytes{get;}=measuredBytes; }
    public sealed record ActiveCommand(int SessionSpid, DateTime StartedUtc, DateTime? EndedUtc, long ElapsedMilliseconds)
    {
        public bool IsActive => SessionSpid > 0 && StartedUtc.Year >= 2020 &&
            (EndedUtc is null || EndedUtc.Value.Year <= 1900) && ElapsedMilliseconds >= 0;
    }

    public static string Workload(string marker)
    {
        if (marker.Length != 42 || !marker.StartsWith("SA_Cancel_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(marker[10..], "N", out _)) throw new InvalidDataException();
        // Fixed finite synthetic computation, one result cell, no model values or writes.
        return "EVALUATE ROW(\"" + marker + "\", SUMX(GENERATESERIES(1,5000000,1), " +
            "SIN([Value]) + COS([Value]) + LOG([Value]+1) + SQRT([Value])))";
    }

    public static bool WasStartedForQuery(ActiveCommand command,DateTimeOffset issuedUtc,TimeSpan? deadline=null)
    {
        var sampledUtc=new DateTimeOffset(command.StartedUtc.AddMilliseconds(command.ElapsedMilliseconds));
        return command.IsActive && command.StartedUtc>=issuedUtc.UtcDateTime.AddMilliseconds(-5) && sampledUtc>=issuedUtc &&
            (deadline is null || command.StartedUtc<=issuedUtc.Add(deadline.Value).UtcDateTime);
    }

    public static async Task<int> RunAsync(string recordPath)
    {
        var stage="owned_process";
        var gates=new List<object>();
        var callerVerified=false;var deadlineVerified=false;
        var evidence=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordPath))!,"owned-cancellation-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            if(new FileInfo(recordPath).Length>16_384) throw new InvalidDataException();
            var owned=JsonSerializer.Deserialize<OwnedDesktopVerification.OwnedSession>(await File.ReadAllTextAsync(recordPath)) ?? throw new InvalidDataException();
            void Owned()=>OwnedDesktopVerification.EnsureOwned(owned);
            Owned();
            var discovery=new DesktopDiscovery(new PinnedFixturePortInventory(new WindowsLocalPortInventory(),owned.EngineProcessId,owned.EngineStartedUtcTicks,Owned));
            var instance=discovery.Discover([Path.GetDirectoryName(owned.Workspace)!]).Single(i=>i.Workspace.Equals(Path.GetFullPath(owned.Workspace),StringComparison.OrdinalIgnoreCase));
            void Ensure(){Owned();discovery.EnsureCurrent(instance);}
            void Pass(object? details=null){Ensure();gates.Add(new{Gate=stage,Pass=true,Details=details});}
            Pass();
            using var total=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var server=new Server();server.Connect("Data Source=localhost:"+instance.Owner.Port+";Connect Timeout=3");
            if(server.Databases.Count!=1) throw new InvalidDataException();
            stage="fixed_inline_fixture";OwnedQueryVerification.ValidateFixture(server.Databases[0]);Pass();
            var adapter=new DesktopModelAdapter(instance,server.Databases[0].ID,discovery);
            var snapshot=await adapter.ReadSnapshotAsync(total.Token);
            var builder=new DbConnectionStringBuilder { ["Data Source"]="localhost:"+instance.Owner.Port,["Initial Catalog"]=server.Databases[0].ID,["Connect Timeout"]=3 };
            using var monitor=new AdomdConnection(builder.ConnectionString);monitor.Open();Ensure();
            await Verify(false);
            await Verify(true);
            stage="fresh_query_and_unchanged_fixture";
            var recovery=await adapter.ExecuteAsync(snapshot.Identity,"EVALUATE ROW(\"Revenue\",SUM('Sales'[Revenue amount]))",new(),total.Token);
            if(recovery.Truncated || recovery.Rows.Length!=1 || recovery.Rows[0][0].GetDecimal()!=700m ||
                (await adapter.ReadSnapshotAsync(total.Token)).Identity!=snapshot.Identity) throw new InvalidDataException();
            Pass();await Save(true);Console.WriteLine(evidence);return 0;

            async Task Verify(bool ownDeadline)
            {
                var name=ownDeadline?"adapter_deadline":"caller_cancel";
                var marker="SA_Cancel_"+Guid.NewGuid().ToString("N");
                var dax=Workload(marker);
                using var caller=CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                var issuedUtc=DateTimeOffset.UtcNow;
                var watch=Stopwatch.StartNew();
                var work=adapter.ExecuteAsync(snapshot.Identity,dax,new(1,1024,TimeSpan.FromSeconds(ownDeadline?2:10)),caller.Token);
                var terminal=false;
                try
                {
                    stage=name+"_observe_active";
                    ActiveCommand? active=null;
                    while(watch.Elapsed<TimeSpan.FromSeconds(8) && !work.IsCompleted)
                    {
                        active=await Observe();
                        if(active?.IsActive==true && (ownDeadline || !work.IsCompleted)) break;
                        await Task.Delay(20,total.Token);
                    }
                    if(active?.IsActive!=true || (!ownDeadline && work.IsCompleted))
                    {
                        gates.Add(new{Gate=name+"_observation_diagnostic",Pass=false,QueryStatus=work.Status.ToString(),
                            ElapsedMilliseconds=watch.Elapsed.TotalMilliseconds,LastMarker=active,
                            QueryErrorType=work.Exception?.GetBaseException().GetType().Name,
                            QueryErrorHResult=work.Exception?.GetBaseException().HResult.ToString("X8")});
                        throw new ProbeFailure("active_query_not_observed");
                    }
                    var observedAt=watch.Elapsed.TotalMilliseconds;
                    // All processes are on this same pinned Windows host; DMV times are UTC.
                    // A deadline snapshot may arrive after cancellation, but it must describe
                    // a command that started before the deadline. Cancellation propagation
                    // is asynchronous; a still-active snapshot after that deadline is valid.
                    var sampledUtc=new DateTimeOffset(active.StartedUtc.AddMilliseconds(active.ElapsedMilliseconds));
                    if(!WasStartedForQuery(active,issuedUtc,ownDeadline?TimeSpan.FromSeconds(2):null))
                        throw new ProbeFailure("active_snapshot_outside_query_window");
                    if(!ownDeadline) caller.Cancel();
                    Pass(new{ObservedAtMilliseconds=observedAt,QueryIssuedUtc=issuedUtc,ServerSampledUtc=sampledUtc,
                        active.SessionSpid,active.StartedUtc,active.EndedUtc,active.ElapsedMilliseconds,
                        ReceivedAfterDeadline=ownDeadline && observedAt>=2000,
                        ServerSampleAfterDeadline=ownDeadline && sampledUtc>issuedUtc.AddSeconds(2)});
                    stage=name+"_cancelled_without_result";
                    try {await work;terminal=true;throw new InvalidDataException("Cancelled query returned a result.");}
                    catch(OperationCanceledException) when (!total.IsCancellationRequested && (ownDeadline?!caller.IsCancellationRequested:caller.IsCancellationRequested)) {terminal=true;}
                    if(ownDeadline && watch.Elapsed<TimeSpan.FromSeconds(2)) throw new ProbeFailure("cancelled_before_own_deadline");
                    Pass(new{CallerRequestedCancellation=!ownDeadline,AdapterTimeoutSeconds=ownDeadline?2:10,
                        ElapsedMilliseconds=watch.Elapsed.TotalMilliseconds,ResultPublished=false});
                    stage=name+"_no_active_marker";
                    var stopWatch=Stopwatch.StartNew();
                    ActiveCommand? remaining;
                    do
                    {
                        remaining=await Observe();
                        if(remaining?.IsActive!=true) break;
                        await Task.Delay(20,total.Token);
                    }while(stopWatch.Elapsed<TimeSpan.FromSeconds(5));
                    if(remaining?.IsActive==true) throw new InvalidDataException("Own command remained active.");
                    Pass(new{ActiveMarkerAfterCancellation=false,ObservationMilliseconds=stopWatch.Elapsed.TotalMilliseconds,
                        ServerReportedCancellationReason=false});
                    if(ownDeadline) deadlineVerified=true;else callerVerified=true;
                }
                finally
                {
                    if(!terminal)
                    {
                        caller.Cancel();
                        try {await work;}catch(OperationCanceledException){}catch{ }
                    }
                }
                Task<ActiveCommand?> Observe()=>Task.Factory.StartNew(()=>ReadMarker(monitor,marker,Ensure,total.Token),
                    total.Token,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            }
        }
        catch(Exception error)
        {
            gates.Add(new{Gate=stage,Pass=false,ErrorType=error.GetType().Name,Code=(error as ProbeFailure)?.Code,
                MeasuredCharacters=(error as ProbeFailure)?.MeasuredCharacters,MeasuredBytes=(error as ProbeFailure)?.MeasuredBytes});
            await Save(false);Console.Error.WriteLine(evidence);return 1;
        }
        async Task Save(bool passed)
        {
            using var file=new FileStream(evidence,FileMode.CreateNew,FileAccess.Write);
            await JsonSerializer.SerializeAsync(file,new{Version=1,CapturedUtc=DateTimeOffset.UtcNow,Synthetic=true,
                ReadOnly=true,Passed=passed,InFlightQueryCancellationVerified=callerVerified,AdapterInFlightDeadlineVerified=deadlineVerified,
                ObservedActiveCommandBeforeCallerCancellation=callerVerified,CancelledQueryResultDiscarded=callerVerified,
                NoActiveMatchingCommandAfterCancellation=callerVerified && deadlineVerified,EngineAbortCauseVerified=false,
                ServerReportedCancellationReason=false,ActualProcessRaceVerified=false,NativeAssistantUiVerified=false,
                PrivateModelsAccessed=false,RealAiProviderVerified=false,CloudVerified=false,
                WorkloadSeriesRows=5_000_000,MaximumConfiguredQueryTimeoutSeconds=10,TotalCancellationDeadlineSeconds=45,Gates=gates},new JsonSerializerOptions{WriteIndented=true});
        }
    }

    private static ActiveCommand? ReadMarker(AdomdConnection connection,string marker,Action ensure,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ensure();
        using var command=connection.CreateCommand();command.CommandTimeout=2;
        command.CommandText="SELECT TOP 128 SESSION_SPID, COMMAND_START_TIME, COMMAND_END_TIME, COMMAND_ELAPSED_TIME_MS, COMMAND_TEXT FROM $SYSTEM.DISCOVER_COMMANDS";
        using var cancel=token.Register(()=>{try{command.Cancel();}catch{}});
        using var reader=command.ExecuteReader();
        var sessionColumn=reader.GetOrdinal("SESSION_SPID");var startColumn=reader.GetOrdinal("COMMAND_START_TIME");
        var endColumn=reader.GetOrdinal("COMMAND_END_TIME");var elapsedColumn=reader.GetOrdinal("COMMAND_ELAPSED_TIME_MS");
        var textColumn=reader.GetOrdinal("COMMAND_TEXT");
        ActiveCommand? found=null;var rows=0;var textBytes=0;
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();if(++rows>128) throw new ProbeFailure("dmv_row_limit");
            var text=reader.IsDBNull(textColumn)?"":reader.GetString(textColumn);
            textBytes+=System.Text.Encoding.UTF8.GetByteCount(text);
            // The owned engine may retain a large prior TOM command. This is a
            // diagnostic-only budget, separate from product QueryResult limits.
            if(text.Length>256_000 || textBytes>2_000_000) throw new ProbeFailure("dmv_text_limit",text.Length,textBytes);
            if(!text.Contains(marker,StringComparison.Ordinal)) continue;
            if(text.Length>8192) throw new ProbeFailure("marked_command_text_limit",text.Length,textBytes);
            if(found is not null) throw new ProbeFailure("ambiguous_marker");
            var start=DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(startColumn),CultureInfo.InvariantCulture),DateTimeKind.Utc);
            var end=reader.IsDBNull(endColumn)?(DateTime?)null:DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(endColumn),CultureInfo.InvariantCulture),DateTimeKind.Utc);
            found=new(Convert.ToInt32(reader.GetValue(sessionColumn),CultureInfo.InvariantCulture),start,end,Convert.ToInt64(reader.GetValue(elapsedColumn),CultureInfo.InvariantCulture));
        }
        token.ThrowIfCancellationRequested();ensure();return found;
    }
}
