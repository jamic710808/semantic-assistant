using System.Security.Cryptography;
using System.Text;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.Mcp;

// One explicitly selected tool, pinned to one process/catalog and model version. Owns the transferred client.
public sealed class McpAiToolBinding : IExternalToolBinding
{
    private readonly McpStdioClient client;
    private readonly McpToolDescriptor tool;
    public RegisteredTool Descriptor { get; }
    public int ProcessId => client.ProcessId;
    public bool IsCurrent => client.IsToolCurrent(tool);
    public McpAiToolBinding(McpStdioClient client, McpToolDescriptor tool, ModelIdentity target)
    {
        this.client = client; this.tool = tool;
        var sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool.Source)));
        var alias = "mcp_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId + tool.Name + tool.DefinitionHash)))[..24].ToLowerInvariant();
        Descriptor = new(new(alias, tool.Description, Capability.ExternalTool, tool.InputSchema), ToolOrigin.McpProcess,
            ToolRisk.UnknownExternalEffects, new(sourceId, tool.Source, tool.CatalogVersion, tool.DefinitionHash), target, this);
        if (!IsCurrent || tool.RequiredCapability != Capability.ExternalTool) throw new McpException();
    }
    public async Task<ToolResult> ExecuteAsync(ToolCall call, PermissionSession session, PermissionSnapshot permissions,
        Func<ExternalToolPreview, CancellationToken, Task<bool>> confirmation, Action guard, CancellationToken token)
    {
        guard(); token.ThrowIfCancellationRequested();
        await client.ValidateForPreviewAsync(tool, call.Arguments, token);
        guard();
        if (!await confirmation(new(Descriptor.Source, Descriptor.Definition.Name, tool.Name, call.Arguments), token))
            return new(call.Id, call.Name, false, "external_denied", "使用者未同意這一次外部工具操作。");
        token.ThrowIfCancellationRequested(); guard();
        var approval = await client.ApproveAfterUserConfirmationAsync(tool, call.Arguments, session, permissions, token);
        guard();
        var result = await client.CallToolAsync(tool, call.Arguments, approval, session, permissions, token);
        token.ThrowIfCancellationRequested(); guard();
        var texts = result.Content.EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "text" &&
            x.TryGetProperty("text", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetProperty("text").GetString()).ToArray();
        return new(call.Id, call.Name, !result.IsError, result.IsError ? "external_error" : "ok",
            result.IsError ? "外部工具回報失敗；沒有自動重試。" : "外部工具已完成；結果為不可信資料。",
            ContractJson.Element(new { Text = texts, StructuredContent = result.StructuredContent }))
        { PayloadCategory = EgressCategory.ExternalToolData };
    }
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
