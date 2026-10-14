using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// Metadata only: no DAX evaluation, data rows, ownership requirement or write service.
public static class ModelOverview
{
    public static ToolDefinition Definition { get; } = new("get_model_overview",
        "唯讀取得整個模型的表／欄位／度量值清單與數量、關係端點、啟用狀態、基數、篩選方向及未連接表。公式細節另用get_table_details。",
        Capability.MetadataRead, ContractJson.Element(new
        {
            type = "object", additionalProperties = false,
            properties = new Dictionary<string, object>()
        }));

    public static void ValidateArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new ToolInputException("get_model_overview 只接受空物件參數 {}。");
    }

    public static JsonElement Create(ModelSnapshot snapshot)
    {
        var connected = snapshot.Relationships.SelectMany(r => new[] { r.FromTable, r.ToTable })
            .ToHashSet(StringComparer.Ordinal);
        var relationshipCoverageComplete = snapshot.UnsupportedRelationshipNames.IsEmpty;
        var unconnected = snapshot.Tables.Where(t => !connected.Contains(t.Name)).Select(t => t.Name).ToArray();
        return ContractJson.Element(new
        {
            schemaVersion = 1,
            source = snapshot.Identity,
            counts = new
            {
                tables = snapshot.Tables.Length,
                columns = snapshot.Tables.Sum(t => t.Columns.Length),
                measures = snapshot.Tables.Sum(t => t.Measures.Length),
                relationships = snapshot.Relationships.Length + snapshot.UnsupportedRelationshipNames.Length,
                projectedRelationships = snapshot.Relationships.Length
            },
            tables = snapshot.Tables.Select(t => new
            {
                t.Name,
                columns = t.Columns.Select(c => new { c.Name, c.DataType, isCalculated = c.Expression is not null }),
                measures = t.Measures.Select(m => m.Name)
            }),
            relationships = snapshot.Relationships,
            relationshipCoverageComplete,
            unsupportedRelationships = snapshot.UnsupportedRelationshipNames,
            disconnectedTables = relationshipCoverageComplete ? unconnected : [],
            tablesWithoutProjectedRelationships = unconnected,
            limitations = new[]
            {
                "只含模型結構，不含報表頁面、視覺、資料列、日期表標記、欄位排序或角色／RLS定義。",
                "From／To是原生關係端點，不等於篩選方向。請同時核對IsActive、FromCardinality、ToCardinality、CrossFiltering；null表示未知。",
                "未連接表的業務用途不能只憑名稱確定；需公式／報表使用證據。若關係投影不完整，不能宣稱表完全未連接。"
            }
        });
    }
}
