using System.Text;
using System.Text.Json;

namespace SemanticAssistant.Adapters.Mcp;

// Bounded tree walks only. Schema compilation, references and regex evaluation run in the worker.
public static class McpSchemaPolicy
{
    public static void CheckSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(schema.GetRawText()) > 16000) throw new McpException();
        using var document = JsonDocument.Parse(schema.GetRawText(), new() { MaxDepth = 32 });
        CheckTree(document.RootElement, true);
    }
    public static void CheckArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 16000) throw new McpException();
        using var document = JsonDocument.Parse(arguments.GetRawText(), new() { MaxDepth = 16 });
        CheckTree(document.RootElement, false);
    }
    private static void CheckTree(JsonElement value, bool references)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new McpException();
                if (references && field.Name is "$ref" or "$dynamicRef" &&
                    (field.Value.ValueKind != JsonValueKind.String || !field.Value.GetString()!.StartsWith('#'))) throw new McpException();
                CheckTree(field.Value, references);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckTree(item, references);
    }
}
