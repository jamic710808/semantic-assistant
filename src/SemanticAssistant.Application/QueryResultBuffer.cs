using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// One lifetime and identity for all size probes and the published result.
public sealed class QueryResultBuffer
{
    private readonly QueryLimits limits;
    private readonly QueryResult template;
    private readonly ImmutableArray<ImmutableArray<JsonElement>>.Builder rows = ImmutableArray.CreateBuilder<ImmutableArray<JsonElement>>();
    private bool truncated;

    public QueryResultBuffer(ModelIdentity source, ImmutableArray<string> columns, string dax, QueryLimits limits)
    {
        if (limits.MaxRows is < 1 or > 100 || limits.MaxBytes is < 1024 or > 64_000 ||
            columns.IsDefault || columns.Length > 64) throw new ArgumentOutOfRangeException(nameof(limits));
        this.limits = limits;
        template = new(Guid.NewGuid().ToString("N"), source, columns, [], false, dax);
        // Headers, source, query and lifetime fields consume the same budget as values.
        if (!Fits(template)) throw new InvalidDataException("查詢結果封裝超過大小上限。");
    }

    public bool TryAdd(ImmutableArray<JsonElement> row) => TryAdd(() => row);

    public bool TryAdd(Func<ImmutableArray<JsonElement>> readRow)
    {
        if (truncated) return false;
        if (rows.Count >= limits.MaxRows) { truncated = true; return false; }
        var row = readRow();
        if (template.Columns.IsEmpty || row.IsDefault || row.Length != template.Columns.Length ||
            row.Any(v => v.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined))
            throw new InvalidDataException("查詢資料列格式無效。");
        rows.Add(row);
        // false is one byte longer than true; budget for the larger representation.
        if (!Fits(template with { Rows = rows.ToImmutable() }))
        { rows.RemoveAt(rows.Count - 1); truncated = true; return false; }
        return true;
    }

    public QueryResult Build()
    {
        var result = template with { Rows = rows.ToImmutable(), Truncated = truncated };
        if (!Fits(result)) throw new InvalidDataException("查詢結果封裝超過大小上限。");
        return result;
    }

    private bool Fits(QueryResult result) => JsonSerializer.SerializeToUtf8Bytes(result).Length <= limits.MaxBytes;
}
