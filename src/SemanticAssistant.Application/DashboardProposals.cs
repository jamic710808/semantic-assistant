using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// Source and hash are assigned by the host after tool validation, never trusted from AI arguments.
public sealed record DashboardProposal(ModelIdentity Source, DashboardPlan Plan, string Hash);

public static class DashboardProposals
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    public static ToolDefinition Definition { get; } = new("propose_dashboard",
        "提出 Dashboard 計畫供使用者檢視。只驗證結構，不修改模型、不查詢數值、不授予寫入權限。使用 get_table_details 確認欄位，Metrics為待預覽的新自有度量值；Period.End為排除結束日。", Capability.MetadataRead,
        JsonDocument.Parse("""
        {
          "type":"object","additionalProperties":false,"required":["plan"],
          "$defs":{
            "field":{"type":"object","additionalProperties":false,"required":["Table","Column"],"properties":{
              "Table":{"type":"string","minLength":1,"maxLength":128},"Column":{"type":"string","minLength":1,"maxLength":128}}}
          },
          "properties":{"plan":{"type":"object","additionalProperties":false,"required":["Version","Title","Grouping","Metrics","Visuals"],"properties":{
            "Version":{"const":1},"Title":{"type":"string","minLength":1,"maxLength":200},"Grouping":{"$ref":"#/$defs/field"},
            "Period":{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,"required":["Field","Start","End"],"properties":{
              "Field":{"$ref":"#/$defs/field"},"Start":{"type":"string","format":"date"},"End":{"type":"string","format":"date"}}}]},
            "Metrics":{"type":"array","minItems":1,"maxItems":6,"items":{"type":"object","additionalProperties":false,"required":["Table","Name","Expression"],"properties":{
              "Table":{"type":"string","minLength":1,"maxLength":128},"Name":{"type":"string","minLength":1,"maxLength":128},"Expression":{"type":"string","minLength":1,"maxLength":8000}}}},
            "Visuals":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object","additionalProperties":false,"required":["Id","Title","Kind"],"properties":{
              "Id":{"type":"string","minLength":1,"maxLength":64},"Title":{"type":"string","minLength":1,"maxLength":120},
              "Kind":{"enum":["Table","Kpi","BarChart","LineChart"]},"LabelColumn":{"type":["integer","null"],"minimum":0,"maximum":6},
              "ValueColumn":{"type":["integer","null"],"minimum":0,"maximum":6},"Aggregation":{"enum":["Sum","Count","First",null]}}}}
          }}}
        }
        """).RootElement.Clone());

    public static DashboardPlan ParseArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1 ||
            !arguments.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("提案參數只能包含plan物件。");
        return DashboardPlans.Parse(plan.GetRawText());
    }
    public static JsonElement PlanPayload(DashboardPlan plan) => JsonSerializer.SerializeToElement(plan, Options);
    public static DashboardProposal Accept(ToolResult result, ModelSnapshot model)
    {
        if (!result.Success || result.ToolName != Definition.Name || result.Payload is null || result.Query is not null)
            throw new InvalidDataException("不是已接受的Dashboard提案。");
        var plan = DashboardPlans.Parse(result.Payload.Value.GetRawText());
        DashboardPlans.Validate(plan, model);
        return new(model.Identity, plan, Hash(model.Identity, plan));
    }
    public static void Validate(DashboardProposal proposal, ModelSnapshot model)
    {
        if (proposal.Source != model.Identity || proposal.Hash != Hash(proposal.Source, proposal.Plan)) throw new DefinitionConflictException();
        DashboardPlans.Validate(proposal.Plan, model);
    }
    private static string Hash(ModelIdentity source, DashboardPlan plan) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { source, plan })));
}
