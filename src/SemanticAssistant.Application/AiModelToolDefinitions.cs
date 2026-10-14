using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public static class AiModelToolDefinitions
{
    private static object Text(int max) => new { type = "string", minLength = 1, maxLength = max };
    private static ToolDefinition Tool(string name, string description, Capability capability, object properties, string[] required)
        => new(name, description, capability, JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties, required }));
    public static ImmutableArray<ToolDefinition> Definitions { get; } = Build();
    public static bool Contains(string name) => Definitions.Any(t => t.Name == name);
    public static bool IsMutation(string name) => Definitions.Any(t => t.Name == name && t.RequiredCapability == Capability.DefinitionWrite);
    private static ImmutableArray<ToolDefinition> Build()
    {
        var tools = ImmutableArray.CreateBuilder<ToolDefinition>();
        foreach (var name in new[] { "add_measure", "update_measure" })
            tools.Add(Tool(name, (name.StartsWith("add", StringComparison.Ordinal) ? "提出新增度量值；名稱必須尚未存在。" : "提出更新本程式自有度量值；非自有與Dashboard管理物件拒絕。") +
                "回傳待確認，不代表寫入。完成回答後由本機預覽、同意、寫入及回讀。", Capability.DefinitionWrite,
                new { table = Text(128), name = Text(128), expression = Text(8000) }, ["table", "name", "expression"]));
        foreach (var name in new[] { "add_calculated_column", "update_calculated_column" })
            tools.Add(Tool(name, "提出新增／更新自有計算欄位。新增名稱不可存在，更新須已有自有計算欄位。需另行預覽與同意，真實引擎會重算。", Capability.DefinitionWrite,
                new { table = Text(128), name = Text(128), expression = Text(8000), data_type = new { type = "string", @enum = new[] { "String", "Int64", "Double", "Decimal", "Boolean", "DateTime" } } },
                ["table", "name", "expression", "data_type"]));
        var field = new { type = "object", additionalProperties = false, properties = new { Table = Text(128), Column = Text(128) }, required = new[] { "Table", "Column" } };
        foreach (var name in new[] { "add_relationship", "update_relationship" })
            tools.Add(Tool(name, "提出新增／更新自有單欄關係。新增名稱不能存在，更新須為自有關係；不改RLS安全篩選。需另行預覽與同意。", Capability.DefinitionWrite,
                new { name = Text(128), from = field, to = field, is_active = new { type = "boolean" },
                    from_cardinality = new { type = "string", @enum = new[] { "One", "Many" } }, to_cardinality = new { type = "string", @enum = new[] { "One", "Many" } },
                    cross_filtering = new { type = "string", @enum = new[] { "OneDirection", "BothDirections", "Automatic" } } },
                ["name", "from", "to", "is_active", "from_cardinality", "to_cardinality", "cross_filtering"]));
        tools.Add(Tool("get_model_edit_context", "讀取指定表的自有物件、關係與Dashboard管理狀態，作為新增／更新前的依據。", Capability.MetadataRead, new { table = Text(128) }, ["table"]));
        tools.Add(Tool("list_dashboard_measures", "列出本機有部署紀錄且與目前模型物件核對的Dashboard；Verified=false不可更新。只讀定義，不查詢數值。", Capability.MetadataRead, new { }, []));
        tools.Add(Tool("get_dashboard_definition", "讀回指定Dashboard完整計畫、HTML度量值、Revision及Verified狀態。更新前必須讀此版本。", Capability.MetadataRead,
            new { dashboard_id = Text(36) }, ["dashboard_id"]));
        var planSchema = DashboardProposals.Definition.Parameters.GetProperty("properties").GetProperty("plan").Clone();
        var defs = DashboardProposals.Definition.Parameters.GetProperty("$defs").Clone();
        foreach (var name in new[] { "generate_dashboard_measures", "update_dashboard_measures" })
        {
            var update = name.StartsWith("update", StringComparison.Ordinal);
            var properties = new Dictionary<string, object> { ["plan"] = planSchema };
            if (update) { properties["dashboard_id"] = Text(36); properties["expected_revision"] = Text(64); }
            var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["type"] = "object", ["additionalProperties"] = false, ["$defs"] = defs, ["properties"] = properties,
                ["required"] = update ? new[] { "plan", "dashboard_id", "expected_revision" } : new[] { "plan" } });
            tools.Add(new(name, update
                ? "提出更新已部署Dashboard。先get_dashboard_definition，傳同一dashboard_id及expected_revision。完整plan保留未要求變動的指標名稱。修改指標與動態HTML，一次預覽後同意；沒有刪除舊指標。回傳pending，不代表完成。"
                : "提出部署新Dashboard：新指標名稱必須尚未存在；本機會產生動態HTML度量值與部署登記。先get_table_details。回傳pending，桌面同意及回讀後才算完成。",
                Capability.DefinitionWrite, schema));
        }
        return tools.ToImmutable();
    }
}
