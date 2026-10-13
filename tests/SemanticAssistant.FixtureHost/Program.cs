using System.Text;
using System.Text.Json;

// Self-authored, synthetic stdio server. Never connects to Power BI or reads credentials.
Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault() == "--mcp-schema-worker-v1")
{
    // Deliberately uncooperative OWN helper for timeout/Job CPU tests, never a vendor process.
    using var workerInput = JsonDocument.Parse(await Console.In.ReadToEndAsync());
    var workerRoot = workerInput.RootElement;
    var workerSchema = workerRoot.GetProperty("schema");
    if (workerSchema.TryGetProperty("x-fixture-hang", out _)) { await Task.Delay(30000); return; }
    if (workerSchema.TryGetProperty("x-fixture-spin", out _))
    {
        ulong counter = 1;
        while (true) { counter = unchecked(counter * 1664525 + 1013904223); if (counter == 0) Console.Error.Write('.'); }
    }
    if (workerSchema.TryGetProperty("x-fixture-bad-response", out _)) { Console.WriteLine("synthetic-sensitive-schema-error"); return; }
    if (workerSchema.TryGetProperty("x-fixture-oversize-response", out _)) { Console.WriteLine(new string('x', 1024)); return; }
    Console.WriteLine(JsonSerializer.Serialize(new { version = 1, nonce = "wrong-nonce", valid = true }));
    return;
}
var mode = args.FirstOrDefault() ?? "normal";
var calls = 0;
Console.Error.WriteLine("synthetic-stderr-secret-not-for-log");
while (await Console.In.ReadLineAsync() is { } line)
{
    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    if (!root.TryGetProperty("id", out var id)) continue;
    object result;
    switch (root.GetProperty("method").GetString())
    {
        case "initialize":
            result = new
            {
                protocolVersion = mode == "bad-version" ? "future-unknown" : "2025-11-25",
                capabilities = new { tools = new { listChanged = true } },
                serverInfo = new { name = "SyntheticFixture", version = "1" }
            };
            break;
        case "tools/list":
            if (mode == "bad-json") { Console.WriteLine("stdout-is-not-json"); continue; }
            if (mode == "oversize") { Console.WriteLine(new string('x', 64001)); continue; }
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { message = new { type = "string", maxLength = 100 } },
                required = new[] { "message" },
                additionalProperties = false
            });
            if (mode == "remote-ref") schema = JsonDocument.Parse("{\"type\":\"object\",\"$ref\":\"https://example.invalid/schema\"}").RootElement.Clone();
            result = new
            {
                tools = new[] { new { name = "synthetic.echo", description = "合成 echo；annotations 不授予權限", inputSchema = schema,
                annotations = new { readOnlyHint = true, destructiveHint = false } } }
            };
            break;
        case "tools/call":
            if (mode == "hang") { await Task.Delay(30000); continue; }
            if (mode == "change") Console.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
            if (mode == "remote-error")
            { Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -1, message = "synthetic-sensitive-error" } })); continue; }
            var message = root.GetProperty("params").GetProperty("arguments").GetProperty("message").GetString();
            calls++;
            result = mode == "counting" ? new { content = new[] { new { type = "text", text = "合成結果：" + message } }, isError = false, structuredContent = new { Calls = calls } } :
                (object)new { content = new[] { new { type = "text", text = "合成結果：" + message } }, isError = false };
            break;
        default:
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "unsupported" } })); continue;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
}
