using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public enum VisualKind { Table, Kpi, BarChart, LineChart }
public enum Aggregation { Sum, Count, First }
public sealed record VisualSpec([property: JsonRequired] string Id, [property: JsonRequired] string Title,
    [property: JsonRequired] VisualKind Kind, int? LabelColumn,
    int? ValueColumn, Aggregation? Aggregation);
public sealed record ReportSpec([property: JsonRequired] int Version, [property: JsonRequired] string ResultId,
    [property: JsonRequired] string Title, [property: JsonRequired] ImmutableArray<VisualSpec> Visuals);

public static class ReportSpecs
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 16,
    };

    public static ReportSpec Parse(string json, QueryResult source)
    {
        if (Encoding.UTF8.GetByteCount(json) > 32_000) throw new InvalidDataException("報告規格超過上限。");
        try
        {
            using var doc = JsonDocument.Parse(json, new() { MaxDepth = 16 });
            RejectDuplicates(doc.RootElement);
            var spec = JsonSerializer.Deserialize<ReportSpec>(json, Options) ?? throw new InvalidDataException("缺少報告規格。");
            Validate(spec, source);
            return spec;
        }
        catch (JsonException) { throw new InvalidDataException("報告 JSON 不符合 schema。"); }
    }

    public static string Serialize(ReportSpec spec) => JsonSerializer.Serialize(spec, Options);

    public static ReportSpec Default(QueryResult result)
    {
        ValidateSource(result);
        var visuals = ImmutableArray.CreateBuilder<VisualSpec>();
        visuals.Add(new("table", "查詢資料", VisualKind.Table, null, null, null));
        if (result.Rows.IsEmpty) visuals.Add(new("count", "回傳列數", VisualKind.Kpi, null, null, Aggregation.Count));
        var numeric = result.Rows.IsDefaultOrEmpty ? -1 : Enumerable.Range(0, result.Columns.Length)
            .FirstOrDefault(i => result.Rows.All(r => r.Length == result.Columns.Length && r[i].ValueKind == JsonValueKind.Number && r[i].TryGetDecimal(out _)), -1);
        if (numeric >= 0)
        {
            visuals.Add(new("kpi", result.Columns[numeric] + "合計", VisualKind.Kpi, null, numeric, Aggregation.Sum));
            if (result.Columns.Length > 1)
                visuals.Add(new("bar", result.Columns[numeric] + "分布", VisualKind.BarChart, numeric == 0 ? 1 : 0, numeric, null));
        }
        var spec = new ReportSpec(1, result.ResultId, result.Source.Name, visuals.ToImmutable());
        Validate(spec, result);
        return spec;
    }

    public static void Validate(ReportSpec spec, QueryResult source)
    {
        ValidateSource(source);
        if (spec.Version != 1 || string.IsNullOrWhiteSpace(source.ResultId) || spec.ResultId != source.ResultId ||
            string.IsNullOrWhiteSpace(spec.Title) || spec.Title.Length > 200 || spec.Visuals.IsDefaultOrEmpty || spec.Visuals.Length > 8)
            throw new InvalidDataException("報告版本、來源或視覺規格無效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var visual in spec.Visuals)
        {
            if (visual is null || string.IsNullOrWhiteSpace(visual.Id) || visual.Id.Length > 64 || !ids.Add(visual.Id) ||
                string.IsNullOrWhiteSpace(visual.Title) || visual.Title.Length > 120 || !Enum.IsDefined(visual.Kind))
                throw new InvalidDataException("視覺物件識別碼或標題無效。");
            if (visual.Kind == VisualKind.Table)
            {
                if (visual.LabelColumn is not null || visual.ValueColumn is not null || visual.Aggregation is not null)
                    throw new InvalidDataException("表格不接受運算或欄位指定。");
                continue;
            }
            if (visual.Kind == VisualKind.Kpi && visual.Aggregation is { } agg && Enum.IsDefined(agg) && visual.LabelColumn is null)
            {
                if (agg == Aggregation.Count && visual.ValueColumn is null) continue;
            }
            else if (visual.Kind is VisualKind.BarChart or VisualKind.LineChart)
            {
                if (visual.Aggregation is not null || visual.LabelColumn is not { } label || label < 0 || label >= source.Columns.Length || label == visual.ValueColumn)
                    throw new InvalidDataException("圖表欄位無效。");
            }
            else throw new InvalidDataException("KPI 需要受允許的聚合。");
            if (visual.ValueColumn is not { } column || column < 0 || column >= source.Columns.Length || source.Rows.IsEmpty ||
                source.Rows.Any(r => r[column].ValueKind != JsonValueKind.Number || !r[column].TryGetDecimal(out var n) || n < -1_000_000_000_000_000_000_000_000m || n > 1_000_000_000_000_000_000_000_000m))
                throw new InvalidDataException("數值欄位不存在、非數字或超過圖表範圍。");
        }
    }

    private static void ValidateSource(QueryResult source)
    {
        if (source.Columns.IsDefault || source.Columns.Length > 64 || source.Rows.IsDefault || source.Rows.Length > 100 ||
            (source.Columns.IsEmpty && !source.Rows.IsEmpty) ||
            source.Rows.Any(r => r.IsDefault || r.Length != source.Columns.Length || r.Any(v => v.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))) ||
            JsonSerializer.SerializeToUtf8Bytes(source).Length > 64_000)
            throw new InvalidDataException("報告來源資料形狀或大小無效。");
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { if (!names.Add(field.Name)) throw new InvalidDataException("JSON 欄位重複。"); RejectDuplicates(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
