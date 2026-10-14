using System.Collections.Immutable;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed class ToolPolicyException(string code) : InvalidOperationException("工具註冊、來源或當次授權已失效。")
{ public string Code { get; } = code; }

public sealed class ToolPolicyEngine(ToolRegistry registry, PermissionSession session,
    ModeController? modes = null, ModeSnapshot? modeSnapshot = null, ModelModificationPolicy? modificationPolicy = null)
{
    private readonly ModelModificationSnapshot? modification = modificationPolicy?.Snapshot;
    public ToolRegistrySnapshot Snapshot { get; } = registry.Snapshot;
    public RegisteredTool? Resolve(string name) => Snapshot.Tools.FirstOrDefault(t => t.Definition.Name == name);
    public void EnsureCurrent(PermissionSnapshot permissions)
    {
        if (!Enum.IsDefined(permissions.Mode) || (modes is null) != (modeSnapshot is null)) throw new ToolPolicyException("invalid_mode");
        if (!session.IsCurrent(permissions)) throw new ToolPolicyException("stale_permission");
        if (!registry.IsCurrent(Snapshot)) throw new ToolPolicyException("tool_registry_changed");
        if (Snapshot.Tools.Any(t => t.Binding?.IsCurrent == false)) throw new ToolPolicyException("stale_external_tool");
        if (modes is not null)
        {
            modes.EnsureConversation(modeSnapshot!);
            if (modeSnapshot!.Target != permissions.Target || ModeController.Get(modeSnapshot.Mode).PermissionMode != permissions.Mode)
                throw new ModePolicyException();
        }
    }
    public void Check(RegisteredTool tool, PermissionSnapshot permissions)
    {
        EnsureCurrent(permissions);
        if (!Snapshot.Tools.Contains(tool)) throw new ToolPolicyException("unknown_tool");
        if (!permissions.Allows(tool.Definition.RequiredCapability)) throw new ToolPolicyException("permission_denied");
        if (tool.Risk == ToolRisk.ModelWriteProposal && modificationPolicy is null) throw new ToolPolicyException("model_modification_disabled");
        if (tool.Risk is ToolRisk.UnknownExternalEffects or ToolRisk.ModelWriteProposal && modificationPolicy is not null)
        {
            try { modificationPolicy.EnsureAllowed(modification!); }
            catch (ModelModificationDeniedException) { throw new ToolPolicyException("model_modification_disabled"); }
        }
        if (modes is not null) modes.Check(modeSnapshot!, permissions, session, tool.Definition.RequiredCapability);
        if (tool.Origin == ToolOrigin.McpProcess && (tool.Target != permissions.Target || tool.Binding?.IsCurrent != true))
            throw new ToolPolicyException("stale_external_tool");
    }
    public ImmutableArray<ToolDefinition> AllowedTools(PermissionSnapshot permissions) => Snapshot.Tools.Where(t =>
    {
        try { Check(t, permissions); return true; }
        catch (Exception ex) when (ex is ToolPolicyException or ModePolicyException) { return false; }
    }).Select(t => t.Definition).ToImmutableArray();

    public static EgressCategory RequiredEgress(ToolDefinition tool) => tool.RequiredCapability switch
    {
        Capability.MetadataRead => EgressCategory.Metadata,
        Capability.DataQuery => EgressCategory.QueryResults,
        Capability.ExternalTool => EgressCategory.ExternalToolData,
        Capability.DefinitionWrite => EgressCategory.Metadata,
        _ => throw new ToolPolicyException("invalid_tool_capability")
    };
    public static void EnsureCallEgress(ToolCall call, ProviderAuthorization authorization)
    {
        // Native rejected calls echo the provider's own arguments with a fixed denial and no model data.
        // MCP arguments belong to the separately consented external workflow, including direct requests.
        if (call.Name.StartsWith("mcp_", StringComparison.Ordinal) && !authorization.Allows(EgressCategory.ExternalToolData))
            throw new EgressAuthorizationException();
    }
    public static void EnsureResultEgress(ToolResult result, ProviderAuthorization authorization)
    {
        var external = result.ToolName.StartsWith("mcp_", StringComparison.Ordinal);
        if (!external && result.Payload is not null && result.PayloadCategory is not (null or EgressCategory.Metadata))
            throw new EgressAuthorizationException();
        if (external && (!authorization.Allows(EgressCategory.ExternalToolData) || result.Query is not null ||
            result.Payload is not null && result.PayloadCategory != EgressCategory.ExternalToolData)) throw new EgressAuthorizationException();
        if (result.Payload is not null && (result.PayloadCategory is { } payloadCategory && payloadCategory is not (EgressCategory.Metadata or EgressCategory.ExternalToolData) ||
            !authorization.Allows(result.PayloadCategory ?? EgressCategory.Metadata))) throw new EgressAuthorizationException();
        if (result.PayloadCategory is { } category && !Enum.IsDefined(category)) throw new EgressAuthorizationException();
        if (result.Query is not null && !authorization.Allows(EgressCategory.QueryResults)) throw new EgressAuthorizationException();
    }
    public static void EnsureResultFresh(ToolResult result, TimeProvider? clock = null)
    {
        if (result.Query is { } query) QueryResultRetention.EnsureFresh(query, clock);
        if (result.PayloadCategory != EgressCategory.ExternalToolData && !result.ToolName.StartsWith("mcp_", StringComparison.Ordinal)) return;
        var time = clock ?? TimeProvider.System; var elapsed = time.GetElapsedTime(result.ReceivedTimestamp, time.GetTimestamp());
        if (time.GetUtcNow() >= result.ReceivedAt + QueryResult.RetentionPeriod || elapsed < TimeSpan.Zero || elapsed >= QueryResult.RetentionPeriod)
            throw new QueryResultExpiredException();
    }
}
