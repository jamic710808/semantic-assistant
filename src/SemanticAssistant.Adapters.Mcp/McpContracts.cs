using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.Mcp;

public sealed class McpException() : Exception("MCP 程序／協定／授權失效。沒有顯示遠端錯誤或程序環境。");

public sealed class McpProcessConfiguration(string executable, ImmutableArray<string> arguments,
    ImmutableDictionary<string, string>? environment = null, string? workingDirectory = null)
{
    public string Executable { get; } = Path.GetFullPath(executable);
    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(executable))!);
    [JsonIgnore] public ImmutableArray<string> Arguments { get; } = arguments;
    [JsonIgnore] public ImmutableDictionary<string, string> Environment { get; } = environment ?? ImmutableDictionary<string, string>.Empty;
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(Executable) ||
            !Directory.Exists(WorkingDirectory) || (workingDirectory is not null && !Path.IsPathFullyQualified(workingDirectory)) ||
            Arguments.IsDefault || Arguments.Length > 64 || Arguments.Any(a => a.Length > 4096 || a.Contains('\0')) ||
            Environment.Count > 64 || Environment.Any(e => e.Key.Length > 128 || e.Key.Contains('=') || e.Key.Contains('\0') || e.Value.Length > 16384 || e.Value.Contains('\0')))
            throw new McpException();
    }
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { Executable, WorkingDirectory, Arguments, Environment = Environment.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() }))));
    public override string ToString() => "McpProcessConfiguration(arguments/environment redacted)";
}

public sealed record McpToolDescriptor(string Source, long CatalogVersion, string Name, string Description, JsonElement InputSchema,
    string DefinitionHash, Capability RequiredCapability = Capability.ExternalTool);
public sealed record McpInvocationResult(string ToolName, bool IsError, JsonElement Content, JsonElement? StructuredContent);

public sealed class McpInvocationApproval
{
    internal McpInvocationApproval(Guid client, McpToolDescriptor tool, string argumentsHash,
        PermissionSnapshot permissions, DateTimeOffset expiry)
    { Client = client; Tool = tool; ArgumentsHash = argumentsHash; Permissions = permissions; ExpiresAt = expiry; }
    internal Guid Client { get; }
    internal McpToolDescriptor Tool { get; }
    internal string ArgumentsHash { get; }
    internal PermissionSnapshot Permissions { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal int Used;
}
