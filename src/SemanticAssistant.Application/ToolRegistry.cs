using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public enum ToolOrigin { BuiltIn, McpProcess }
public enum ToolRisk { ReadOnlyModel, ProposalOnly, UnknownExternalEffects }
public enum ToolResultFormat { TableMetadataV1, QueryResultV1, DashboardPlanV1, McpDataV1 }
public sealed record ToolSource(string Identity, string Display, long Version, string DefinitionHash);
public sealed record RegisteredTool(ToolDefinition Definition, ToolOrigin Origin, ToolRisk Risk, ToolSource Source,
    ModelIdentity? Target = null, IExternalToolBinding? Binding = null)
{
    public ToolResultFormat ResultFormat => Origin == ToolOrigin.McpProcess ? ToolResultFormat.McpDataV1 : Definition.Name switch
    {
        "get_table_details" => ToolResultFormat.TableMetadataV1,
        "execute_dax_query" => ToolResultFormat.QueryResultV1,
        "propose_dashboard" => ToolResultFormat.DashboardPlanV1,
        _ => throw new ToolPolicyException("unknown_tool")
    };
}
public sealed record ToolRegistrySnapshot(Guid RegistryId, long Version, ImmutableArray<RegisteredTool> Tools);
public sealed record ExternalToolPreview(ToolSource Source, string Alias, string OriginalName, JsonElement Arguments);

public interface IExternalToolBinding : IAsyncDisposable
{
    RegisteredTool Descriptor { get; }
    bool IsCurrent { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, PermissionSession session, PermissionSnapshot permissions,
        Func<ExternalToolPreview, CancellationToken, Task<bool>> confirmation, Action guard, CancellationToken token);
}

// Registration selects availability only. The host owns binding lifetimes and obtains explicit selection.
public sealed class ToolRegistry
{
    private readonly object sync = new();
    private ToolRegistrySnapshot current;
    public ToolRegistry(IEnumerable<ToolDefinition> builtIns)
    {
        var tools = builtIns.Select(d => new RegisteredTool(d, ToolOrigin.BuiltIn,
            d.Name == "propose_dashboard" ? ToolRisk.ProposalOnly : ToolRisk.ReadOnlyModel, new("builtin", "本機內建", 1, "v1"))).ToImmutableArray();
        if (tools.Length > 16 || tools.Select(t => t.Definition.Name).Distinct(StringComparer.Ordinal).Count() != tools.Length ||
            tools.Any(t => !(t.Definition.Name == "execute_dax_query" && t.Definition.RequiredCapability == Capability.DataQuery ||
                t.Definition.Name is "get_table_details" or "propose_dashboard" && t.Definition.RequiredCapability == Capability.MetadataRead) ||
                t.Definition.Name.StartsWith("mcp_", StringComparison.Ordinal))) throw new ArgumentException("內建工具清單無效。");
        current = new(Guid.NewGuid(), 1, tools);
    }
    public ToolRegistrySnapshot Snapshot { get { lock (sync) return current; } }
    public bool IsCurrent(ToolRegistrySnapshot snapshot) { lock (sync) return snapshot == current; }
    public void ImportAfterUserSelection(IExternalToolBinding binding)
    {
        var entry = binding.Descriptor;
        if (!binding.IsCurrent || entry.Binding != binding || entry.Origin != ToolOrigin.McpProcess ||
            entry.Risk != ToolRisk.UnknownExternalEffects || entry.Target is null || entry.Definition.RequiredCapability != Capability.ExternalTool ||
            entry.Source.Version < 1 || string.IsNullOrWhiteSpace(entry.Source.Identity) || entry.Source.Identity.Length > 128 ||
            string.IsNullOrWhiteSpace(entry.Source.Display) || entry.Source.Display.Length > 4096 ||
            entry.Source.DefinitionHash.Length != 64 || !entry.Source.DefinitionHash.All(char.IsAsciiHexDigit) ||
            !entry.Definition.Name.StartsWith("mcp_", StringComparison.Ordinal) || entry.Definition.Name.Length is < 5 or > 64 ||
            entry.Definition.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            entry.Definition.Description.Length > 4000 || entry.Definition.Parameters.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(entry.Definition.Parameters.GetRawText()) > 16000)
            throw new ToolPolicyException("invalid_registration");
        lock (sync)
        {
            if (current.Tools.Count(t => t.Origin != ToolOrigin.BuiltIn) >= 8 || current.Tools.Any(t => t.Definition.Name == entry.Definition.Name))
                throw new ToolPolicyException("registration_limit");
            current = current with { Version = current.Version + 1, Tools = current.Tools.Add(entry) };
        }
    }
    public ImmutableArray<IExternalToolBinding> ClearExternal()
    {
        lock (sync)
        {
            var bindings = current.Tools.Where(t => t.Binding is not null).Select(t => t.Binding!).ToImmutableArray();
            if (!bindings.IsEmpty) current = current with { Version = current.Version + 1, Tools = current.Tools.Where(t => t.Origin == ToolOrigin.BuiltIn).ToImmutableArray() };
            return bindings;
        }
    }
}
