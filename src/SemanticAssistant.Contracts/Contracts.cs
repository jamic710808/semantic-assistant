using System.Collections.Immutable;
using System.Text.Json;

namespace SemanticAssistant.Contracts;

public enum Capability { MetadataRead, DataQuery, ArtifactWrite, ExternalTool, DefinitionWrite }
public enum ConversationMode { Analyze, Report }
public enum EventKind { Started, Text, ToolStarted, ToolCompleted, Completed, Cancelled, Error }
public enum EgressCategory { UserMessage, Metadata, QueryResults, ExternalToolData }
public sealed record ProviderIdentity(string Id, string Endpoint, string Model);
public sealed record EgressConsent(ProviderIdentity Provider, ImmutableHashSet<EgressCategory> Categories,
    DateTimeOffset ExpiresAt);

public sealed record ModelIdentity(string Id, string Name, string Revision, bool IsSynthetic);
public sealed record ColumnDefinition(string Name, string DataType, string? Expression = null);
public sealed record MeasureDefinition(string Name, string Expression);
public sealed record TableDefinition(string Name, ImmutableArray<ColumnDefinition> Columns, ImmutableArray<MeasureDefinition> Measures);
public sealed record RelationshipDefinition(string Name, string FromTable, string FromColumn, string ToTable, string ToColumn, bool IsActive,
    string? FromCardinality = null, string? ToCardinality = null, string? CrossFiltering = null, string? SecurityFiltering = null);
public sealed record ModelSnapshot(ModelIdentity Identity, ImmutableArray<TableDefinition> Tables)
{
    public ImmutableArray<RelationshipDefinition> Relationships { get; init; } = [];
}

// A turn pins its model identity and capabilities. UI changes apply to the next turn.
public sealed record PermissionSnapshot(Guid SessionId, long Version, ModelIdentity Target,
    ConversationMode Mode, ImmutableHashSet<Capability> Granted)
{
    public bool Allows(Capability capability) => Granted.Contains(capability);
    public EgressConsent? Egress { get; init; }
}

public sealed record ToolDefinition(string Name, string Description, Capability RequiredCapability, JsonElement Parameters);
public sealed record ToolCall(string Id, string Name, JsonElement Arguments);
public sealed record QueryResult(string ResultId, ModelIdentity Source, ImmutableArray<string> Columns,
    ImmutableArray<ImmutableArray<JsonElement>> Rows, bool Truncated, string Query)
{
    public static TimeSpan RetentionPeriod => TimeSpan.FromMinutes(5);
    // Getter-only lifetime; cloning a result does not renew it. Never load persisted rows.
    public DateTimeOffset ReceivedAt { get; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset ExpiresAt => ReceivedAt + RetentionPeriod;
    [System.Text.Json.Serialization.JsonIgnore]
    public long ReceivedTimestamp { get; } = TimeProvider.System.GetTimestamp();
}
public sealed record ToolResult(string CallId, string ToolName, bool Success, string Code, string Summary,
    JsonElement? Payload = null, QueryResult? Query = null)
{
    public EgressCategory? PayloadCategory { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset ReceivedAt { get; } = TimeProvider.System.GetUtcNow();
    [System.Text.Json.Serialization.JsonIgnore]
    public long ReceivedTimestamp { get; } = TimeProvider.System.GetTimestamp();
}

// Versioned UI event contract; no raw query rows or secrets in diagnostic events.
public sealed record ConversationEvent(int Version, Guid RequestId, long Sequence, EventKind Kind,
    string Message, string? ToolName = null, string? Code = null);
public sealed record ConversationRequest(Guid RequestId, string Message, ConversationMode Mode, PermissionSnapshot Permissions);
public sealed record ConversationOutcome(Guid RequestId, string Code, ImmutableArray<ToolResult> ToolResults);

public static class ContractJson
{
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
}
