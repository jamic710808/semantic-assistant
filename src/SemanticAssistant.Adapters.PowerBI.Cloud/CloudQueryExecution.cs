using System.Text.Json;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.PowerBI.Cloud;

// The production adapter supplies the actual XMLA snapshot and REST delegates.
// An injectable monotonic clock makes delayed timer callbacks testable offline.
internal static class CloudQueryExecution
{
    internal static async Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits,
        Func<CancellationToken, Task<ModelSnapshot>> readSnapshot,
        Func<CancellationToken, Task<JsonDocument>> executeRest, CancellationToken token,
        TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limits.EffectiveTimeout);
        bool Expired() => clock.GetElapsedTime(started) >= limits.EffectiveTimeout;
        void Check()
        {
            if (Expired()) deadline.Cancel();
            deadline.Token.ThrowIfCancellationRequested();
        }
        try
        {
            Check();
            var before = await readSnapshot(deadline.Token).ConfigureAwait(false);
            Check();
            if (before.Identity != target) throw new CloudServiceException();
            using var response = await executeRest(deadline.Token).ConfigureAwait(false);
            Check();
            var result = CloudModelAdapter.ParseQueryResponse(response.RootElement, target, dax, limits);
            Check();
            var after = await readSnapshot(deadline.Token).ConfigureAwait(false);
            Check();
            if (after.Identity != target) throw new CloudServiceException();
            return result;
        }
        catch (Exception) when (deadline.IsCancellationRequested || Expired())
        {
            deadline.Cancel();
            throw new OperationCanceledException(deadline.Token);
        }
    }
}
