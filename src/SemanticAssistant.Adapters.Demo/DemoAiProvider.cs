using System.Runtime.CompilerServices;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.Demo;

// Deterministic provider exercises the real application dispatcher, without network or API keys.
public sealed class DemoAiProvider : IAiProvider
{
    public bool UsesExternalNetwork => false;
    public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(180, cancellationToken);
        var results = request.Messages.Where(m => m.Result is not null).Select(m => m.Result!).ToArray();
        if (results.Length == 0 && request.Tools.Any(t => t.Name == "get_table_details"))
        {
            yield return new ProviderEvent.Text("我先讀取示範模型的結構。\n");
            yield return new ProviderEvent.Call(new("demo-metadata", "get_table_details", ContractJson.Element(new { table = "銷售" })));
            yield break;
        }
        if (results.Any(r => !r.Success))
        {
            yield return new ProviderEvent.Text("工具未成功；本回合保留錯誤狀態，沒有編造資料。\n");
            yield break;
        }
        if (request.UserMessage.Contains("Dashboard", StringComparison.OrdinalIgnoreCase) || request.UserMessage.Contains("儀表板"))
        {
            if (results.All(r => r.ToolName != DashboardProposals.Definition.Name) && request.Tools.Any(t => t.Name == DashboardProposals.Definition.Name))
            {
                yield return new ProviderEvent.Call(new("demo-dashboard", DashboardProposals.Definition.Name,
                    ContractJson.Element(new { plan = DashboardProposals.PlanPayload(DemoDashboard.Plan) })));
                yield break;
            }
            yield return new ProviderEvent.Text("合成Dashboard提案已送交本機檢查。可點選「檢視AI Dashboard提案」，先預覽再逐次同意修改；尚未讀取數值。\n");
            yield break;
        }
        bool wantsQuery = request.Mode == ConversationMode.Report || request.UserMessage.Contains("分析") ||
            request.UserMessage.Contains("數值") || request.UserMessage.Contains("查詢");
        if (wantsQuery && results.All(r => r.ToolName != "execute_dax_query"))
        {
            if (!request.Tools.Any(t => t.Name == "execute_dax_query"))
            {
                yield return new ProviderEvent.Text("目前未授權數值查詢。請勾選「允許示範數值查詢」後開始新回合。\n");
                yield break;
            }
            yield return new ProviderEvent.Text("接著透過受限工具取得合成數值。\n");
            yield return new ProviderEvent.Call(new("demo-query", "execute_dax_query", ContractJson.Element(new { dax = DemoModel.SupportedQuery })));
            yield break;
        }
        var query = results.Select(r => r.Query).FirstOrDefault(q => q is not null);
        yield return new ProviderEvent.Text(query is null
            ? "示範模型包含銷售與日期兩張表；銷售額度量值以金額加總。這些結構是新專案自行建立的合成範例。\n"
            : $"已取得 {query.Rows.Length} 列合成資料。可在右側查看查詢來源；勾選報告匯出授權後產生離線 HTML。\n");
    }
}
