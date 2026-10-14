using System.Collections.Immutable;
using System.Text;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed class ToolDispatcher(ISemanticModelReader reader, IReadOnlyDaxQueryRunner queryRunner,
    PermissionSession session, QueryLimits? limits = null, TimeProvider? resultClock = null,
    ModeController? modes = null, ModeSnapshot? modeSnapshot = null, ToolRegistry? toolRegistry = null,
    Func<ExternalToolPreview, CancellationToken, Task<bool>>? externalConfirmation = null, ModelModificationPolicy? modificationPolicy = null,
    AiModelOperations? aiOperations = null)
{
    private readonly QueryLimits queryLimits = limits ?? new();
    private readonly ToolPolicyEngine policy = new(toolRegistry ?? new(Definitions), session, modes, modeSnapshot, modificationPolicy);
    public void EnsurePolicyCurrent(PermissionSnapshot permissions) => policy.EnsureCurrent(permissions);

    public static ImmutableArray<ToolDefinition> Definitions { get; } =
    [
        ModelOverview.Definition,
        Define("get_table_details", "讀取指定表的欄位及度量值中繼資料。", Capability.MetadataRead, "table", 128),
        Define("execute_dax_query", "執行唯讀 DAX 查詢；adapter 強制限制列數、大小與時間。", Capability.DataQuery, "dax", 8_000),
        DashboardProposals.Definition,
        .. AiModelToolDefinitions.Definitions
    ];

    private static ToolDefinition Define(string name, string description, Capability capability, string parameter, int maxLength) =>
        new(name, description, capability, ContractJson.Element(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { parameter },
            properties = new Dictionary<string, object> { [parameter] = new { type = "string", minLength = 1, maxLength } }
        }));

    public ImmutableArray<ToolDefinition> AllowedTools(PermissionSnapshot permissions) => policy.AllowedTools(permissions)
        .Where(t => aiOperations is not null || !AiModelToolDefinitions.Contains(t.Name)).ToImmutableArray();

    public async Task<ToolResult> DispatchAsync(ToolCall call, PermissionSnapshot permissions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = policy.Resolve(call.Name);
        if (registered is null) return Failure(call, "unknown_tool", "工具未註冊。");
        var definition = registered.Definition;
        try { policy.Check(registered, permissions); }
        catch (ToolPolicyException ex) { return Failure(call, ex.Code, "工具註冊、來源或授權不符合當次政策。"); }
        catch (ModePolicyException) { return Failure(call, "invalid_mode", "目前模式不允許AI執行此工具。"); }
        try
        {
            if (AiModelToolDefinitions.Contains(call.Name))
            {
                if (aiOperations is null) return Failure(call, "model_tools_unavailable", "目前來源沒有模型修改／Dashboard服務。");
                var native = await aiOperations.DispatchAsync(call, permissions, () => policy.Check(registered, permissions), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested(); policy.Check(registered, permissions);
                if (native.Payload is { } payload && Encoding.UTF8.GetByteCount(payload.GetRawText()) > queryLimits.MaxBytes)
                    return Failure(call, "result_limit", "模型／Dashboard定義超過外送結果預算。");
                return native;
            }
            if (call.Name == "execute_dax_query" && aiOperations?.HasPending == true)
                return Failure(call, "modification_pending", "本回合有尚未寫入的修改；請先完成預覽與同意，下一回合再查詢新物件。");
            if (registered.Origin == ToolOrigin.McpProcess)
            {
                if (externalConfirmation is null) return Failure(call, "approval_required", "尚未提供逐次外部操作同意。");
                var external = await registered.Binding!.ExecuteAsync(call, session, permissions, externalConfirmation,
                    () => { cancellationToken.ThrowIfCancellationRequested(); policy.Check(registered, permissions); }, cancellationToken).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested(); policy.Check(registered, permissions);
                if (external.CallId != call.Id || external.ToolName != call.Name || external.Query is not null ||
                    external.Payload is not null && external.PayloadCategory != EgressCategory.ExternalToolData)
                    return Failure(call, "invalid_provenance", "外部工具結果契約不符。");
                ToolPolicyEngine.EnsureResultFresh(external, resultClock);
                if (Encoding.UTF8.GetByteCount(ContractJson.Element(external).GetRawText()) > queryLimits.MaxBytes)
                    return Failure(call, "result_limit", "外部工具結果超過回傳大小限制。");
                return external;
            }
            DashboardPlan? plan = null;
            string input = "";
            if (call.Name == DashboardProposals.Definition.Name)
            {
                try { plan = DashboardProposals.ParseArguments(call.Arguments); }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
                { return Failure(call, "invalid_arguments", "Dashboard提案參數不符合結構契約。"); }
            }
            else if (call.Name == ModelOverview.Definition.Name) ModelOverview.ValidateArguments(call.Arguments);
            else input = ToolArguments.RequiredString(call.Arguments,
                call.Name == "get_table_details" ? "table" : "dax", call.Name == "get_table_details" ? 128 : 8_000);
            var snapshot = await reader.ReadSnapshotAsync(cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsCurrent(permissions)) return Failure(call, "stale_permission", "本回合的授權已變更。");
            if (snapshot.Identity != permissions.Target) return Failure(call, "target_changed", "目標或模型版本已改變，請重新開始回合。");
            policy.Check(registered, permissions);
            if (call.Name == ModelOverview.Definition.Name)
            {
                var payload = ModelOverview.Create(snapshot);
                if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > queryLimits.MaxBytes)
                    return Failure(call, "result_limit", "完整模型概覽超過結果預算；請讀取已知表的細節，並明確說明未取得完整關係圖。");
                return new(call.Id, call.Name, true, "ok", "已讀取唯讀模型概覽；未執行數值查詢或模型修改。", payload);
            }
            if (plan is not null)
            {
                try { DashboardPlans.Validate(plan, snapshot); }
                catch (InvalidDataException) { return Failure(call, "invalid_plan", "Dashboard提案的欄位、指標、週期或視覺不符合目前模型。"); }
                var payload = DashboardProposals.PlanPayload(plan);
                if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > Math.Min(32000, queryLimits.MaxBytes))
                    return Failure(call, "result_limit", "Dashboard提案超過大小限制。");
                return new(call.Id, call.Name, true, "proposal_ready", "Dashboard提案已驗證結構；尚未修改模型或查詢數值，請開啟提案逐次預覽。", payload);
            }
            if (call.Name == "get_table_details")
            {
                var table = snapshot.Tables.FirstOrDefault(t => t.Name == input);
                if (table is null) return Failure(call, "table_not_found", "模型中找不到指定的表。");
                var payload = ContractJson.Element(table);
                if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > queryLimits.MaxBytes)
                    return Failure(call, "result_limit", "中繼資料結果超過大小上限。");
                return new(call.Id, call.Name, true, "ok", $"已讀取 {table.Name} 的結構。", payload);
            }

            using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queryTimeout.CancelAfter(queryLimits.EffectiveTimeout);
            var result = await queryRunner.ExecuteAsync(permissions.Target, input, queryLimits, queryTimeout.Token)
                .WaitAsync(queryTimeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsCurrent(permissions)) return Failure(call, "stale_permission", "已丟棄撤銷授權後回傳的結果。");
            policy.Check(registered, permissions);
            if (result.Source != permissions.Target || result.Query != input)
                return Failure(call, "invalid_provenance", "查詢結果的來源或查詢識別不符。");
            QueryResultRetention.EnsureFresh(result, resultClock);
            if (string.IsNullOrWhiteSpace(result.ResultId) || result.Columns.IsDefault || result.Rows.IsDefault ||
                (result.Columns.IsEmpty && !result.Rows.IsEmpty) ||
                result.Rows.Any(row => row.Length != result.Columns.Length) ||
                result.Rows.Length > queryLimits.MaxRows || Encoding.UTF8.GetByteCount(ContractJson.Element(result).GetRawText()) > queryLimits.MaxBytes)
                return Failure(call, "result_limit", "adapter 結果結構或大小不符限制。");
            return new(call.Id, call.Name, true, "ok", $"取得 {result.Rows.Length} 列；來源：{result.Source.Name}。", Query: result);
        }
        catch (ToolPolicyException ex)
        { return Failure(call, ex.Code, "工具政策已失效，沒有交付結果。"); }
        catch (ModePolicyException)
        { return Failure(call, "invalid_mode", "模式已變更，沒有交付工具結果。"); }
        catch (QueryResultExpiredException)
        { return Failure(call, "result_expired", "查詢結果已過期，沒有提供資料列。"); }
        catch (ToolInputException ex)
        { return Failure(call, "invalid_arguments", ex.Message); }
        catch (ModelModificationDeniedException)
        { return Failure(call, "model_modification_disabled", "安全模式或政策變更：沒有交付修改提案。"); }
        catch (DefinitionConflictException)
        { return Failure(call, "definition_conflict", "模型版本、物件所有權或Dashboard登記版本不符；沒有寫入。"); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        { return Failure(call, "invalid_definition", "修改參數／登記內容無效、超過限制或無法編譯；沒有寫入。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(call, "query_timeout", "查詢已超時。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception)
        { return Failure(call, "adapter_error", "資料來源執行失敗；沒有回傳內部連線或例外內容。"); }
    }

    private static ToolResult Failure(ToolCall call, string code, string message) => new(call.Id, call.Name, false, code, message);
}
