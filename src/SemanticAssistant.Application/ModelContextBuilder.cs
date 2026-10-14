using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record ContextBudget(int MaxTables = 20, int MaxCharacters = 2_000)
{
    public void Validate()
    {
        if (MaxTables is < 1 or > 20 || MaxCharacters is < 128 or > 2_000)
            throw new ArgumentOutOfRangeException(nameof(ContextBudget));
    }
}

// A host-selected strategy receives names only, never expressions or query rows.
public interface IContextTableSelector
{
    IEnumerable<string> Select(ImmutableArray<string> tableNames);
}

public sealed class FirstContextTables : IContextTableSelector
{
    public IEnumerable<string> Select(ImmutableArray<string> tableNames) => tableNames;
}

public sealed class PreferredContextTables(ImmutableArray<string> preferredNames) : IContextTableSelector
{
    public IEnumerable<string> Select(ImmutableArray<string> tableNames)
    {
        var available = tableNames.ToHashSet(StringComparer.Ordinal);
        foreach (var name in preferredNames)
            if (available.Contains(name)) yield return name;
        foreach (var name in tableNames) yield return name;
    }
}

// Pure formatting: the coordinator must authorize reading and egress before calling this.
// Strategies are trusted host code, not plugins or instructions supplied by the model.
public sealed class ModelContextBuilder(IContextTableSelector? selector = null, ContextBudget? budget = null, string? displayName = null)
{
    private readonly IContextTableSelector tableSelector = selector ?? new FirstContextTables();
    private readonly ContextBudget contextBudget = budget ?? new();

    public string Build(ModelSnapshot snapshot)
    {
        contextBudget.Validate();
        var names = snapshot.Tables.Select(t => t.Name).ToImmutableArray();
        var available = names.ToHashSet(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var result = new StringBuilder();
        var counts = $"總數：表{snapshot.Tables.Length}、欄位{snapshot.Tables.Sum(t => t.Columns.Length)}、度量值{snapshot.Tables.Sum(t => t.Measures.Length)}、關係{snapshot.Relationships.Length + snapshot.UnsupportedRelationshipNames.Length}；";
        result.Append(counts).Append("來源：").Append(JsonSerializer.Serialize(Clip(displayName ?? snapshot.Identity.Name, 128)));
        result.Append("; 合成示範：").Append(snapshot.Identity.IsSynthetic).Append("; 表：");
        // JSON escaping can expand names. Omit rather than emit a cut JSON string.
        if (result.Length > contextBudget.MaxCharacters)
            result.Clear().Append(counts).Append("來源名稱省略；合成示範：")
                .Append(snapshot.Identity.IsSynthetic).Append("; 表：");
        var inspected = 0;
        foreach (var name in tableSelector.Select(names))
        {
            // Also bound work for a mistaken selector yielding duplicate/invented names forever.
            if (++inspected > 256 || selected.Count >= contextBudget.MaxTables) break;
            if (!available.Contains(name) || selected.Contains(name)) continue;
            var encoded = JsonSerializer.Serialize(name);
            var separator = selected.Count == 0 ? "" : ", ";
            if (encoded.Length + separator.Length > contextBudget.MaxCharacters - result.Length) continue;
            result.Append(separator).Append(encoded);
            selected.Add(name);
        }
        // Never split JSON or describe a budget-limited summary as the whole graph.
        const string overviewHint = "\n完整結構用get_model_overview；公式用get_table_details。";
        if (result.Length + overviewHint.Length <= contextBudget.MaxCharacters) result.Append(overviewHint);
        var shownRelationships = 0;
        foreach (var relationship in snapshot.Relationships)
        {
            var line = "\n關係：" + ContractJson.Element(relationship).GetRawText();
            const int coverageReserve = 72;
            if (result.Length + line.Length + coverageReserve > contextBudget.MaxCharacters) continue;
            result.Append(line); shownRelationships++;
        }
        var coverage = $"\n摘要含{shownRelationships}/{snapshot.Relationships.Length}條已投影關係；未支援關係{snapshot.UnsupportedRelationshipNames.Length}條。";
        if (result.Length + coverage.Length <= contextBudget.MaxCharacters) result.Append(coverage);
        return result.ToString();
    }

    private static string Clip(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        var length = maximum;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
