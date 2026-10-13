using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public interface ISemanticModelReader
{
    Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
}

public sealed record QueryLimits(int MaxRows = 100, int MaxBytes = 64_000, TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(3);
}

public interface IReadOnlyDaxQueryRunner
{
    Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken cancellationToken);
}

public sealed record ProviderMessage(string Role, string Content, ToolCall? Call = null, ToolResult? Result = null)
{
    public int Round { get; init; }
}
public sealed record ProviderRequest(string UserMessage, ConversationMode Mode, string Context,
    ImmutableArray<ToolDefinition> Tools, ImmutableArray<ProviderMessage> Messages)
{
    public ProviderAuthorization? Authorization { get; init; }
}
public abstract record ProviderEvent
{
    public sealed record Text(string Value) : ProviderEvent;
    public sealed record Call(ToolCall Value) : ProviderEvent;
}

public interface IAiProvider
{
    bool UsesExternalNetwork { get; }
    ProviderIdentity? Identity => null;
    IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request, CancellationToken cancellationToken);
}

public sealed record TurnLimits(int MaxRounds = 6, int MaxToolCalls = 8, int MaxTextCharacters = 20_000,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(15);
    public void Validate()
    {
        if (MaxRounds < 1 || MaxToolCalls < 1 || MaxTextCharacters < 1 || EffectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TurnLimits));
    }
}

public sealed class ToolInputException(string message) : Exception(message);

public static class ToolArguments
{
    public static string RequiredString(JsonElement arguments, string property, int maxLength)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ToolInputException("工具參數必須是 JSON 物件。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in arguments.EnumerateObject())
            if (member.Name != property || !names.Add(member.Name))
                throw new ToolInputException("工具參數包含未知或重複欄位。");
        if (!arguments.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            throw new ToolInputException($"缺少字串參數 {property}。");
        var value = element.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ToolInputException("工具參數為空或超過長度限制。");
        return value;
    }
}
