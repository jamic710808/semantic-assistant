using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed class QueryResultExpiredException() : InvalidOperationException("查詢結果已過期，請重新查詢。");

// A host clock seam is for deterministic fixtures; provider input cannot choose it.
public static class QueryResultRetention
{
    public static TimeSpan Remaining(QueryResult result, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var elapsed = clock.GetElapsedTime(result.ReceivedTimestamp, clock.GetTimestamp());
        if (elapsed < TimeSpan.Zero) return TimeSpan.Zero;
        var wall = result.ExpiresAt - clock.GetUtcNow();
        var monotonic = QueryResult.RetentionPeriod - elapsed;
        var remaining = wall < monotonic ? wall : monotonic;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static bool IsFresh(QueryResult result, TimeProvider? clock = null) => Remaining(result, clock) > TimeSpan.Zero;

    public static void EnsureFresh(QueryResult result, TimeProvider? clock = null)
    {
        if (!IsFresh(result, clock)) throw new QueryResultExpiredException();
    }
}
