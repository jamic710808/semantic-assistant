using System.Text;
using System.Text.Json;
using Json.Schema;

namespace SemanticAssistant.Adapters.Mcp;

// Called exclusively by CLI --mcp-schema-worker-v1. It has no model/provider/MCP connection.
public static class McpSchemaWorker
{
    public static async Task<int> RunAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false, true); Console.OutputEncoding = new UTF8Encoding(false);
        string? nonce = null;
        try
        {
            var input = Console.OpenStandardInput(); var bytes = new byte[64001]; var count = 0;
            while (true)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count));
                if (read == 0) break;
                count += read; if (count > 64000) return 2;
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new() { MaxDepth = 36 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 || root.GetProperty("version").GetInt32() != 1)
                return 2;
            nonce = root.GetProperty("nonce").GetString(); if (!Guid.TryParseExact(nonce, "N", out _)) return 2;
            var schema = root.GetProperty("schema"); var arguments = root.GetProperty("arguments");
            McpSchemaPolicy.CheckSchema(schema); McpSchemaPolicy.CheckArguments(arguments);
            // Explicit stable dialect, no global registry state and no remote fetch. A new process handles one request.
            var registry = new SchemaRegistry { Fetch = (_, _) => throw new McpException() };
            var dialect = schema.TryGetProperty("$schema", out var declared) ? declared.GetString()?.TrimEnd('#') switch
            {
                "http://json-schema.org/draft-06/schema" or "https://json-schema.org/draft-06/schema" => Dialect.Draft06,
                "http://json-schema.org/draft-07/schema" or "https://json-schema.org/draft-07/schema" => Dialect.Draft07,
                "https://json-schema.org/draft/2019-09/schema" => Dialect.Draft201909,
                "https://json-schema.org/draft/2020-12/schema" => Dialect.Draft202012,
                _ => throw new McpException()
            } : Dialect.Draft202012;
            var compiled = JsonSchema.Build(schema, new BuildOptions { Dialect = dialect, SchemaRegistry = registry });
            var valid = compiled.Evaluate(arguments).IsValid;
            Console.WriteLine(JsonSerializer.Serialize(new { version = 1, nonce, valid }));
            return 0;
        }
        catch
        {
            if (nonce is null) return 2;
            Console.WriteLine(JsonSerializer.Serialize(new { version = 1, nonce, valid = false }));
            return 0; // Never return schema text, arguments, exceptions or paths.
        }
    }
}
