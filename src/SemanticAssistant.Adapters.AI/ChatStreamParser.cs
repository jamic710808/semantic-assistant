using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.AI;

public sealed class ProviderProtocolException() : Exception("AI 串流格式或大小不符合限制。");

public static class ChatStreamParser
{
    private sealed class CallParts
    {
        public string? Id;
        public string? Type;
        public readonly StringBuilder Name = new();
        public readonly StringBuilder Arguments = new();
    }

    public static async IAsyncEnumerable<ProviderEvent> ParseAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var calls = new SortedDictionary<int, CallParts>();
        string? finish = null;
        await foreach (var frame in FramesAsync(stream, cancellationToken))
        {
            if (frame == "[DONE]")
            {
                if (finish is null || (calls.Count > 0 && finish != "tool_calls") ||
                    (calls.Count == 0 && finish != "stop")) throw new ProviderProtocolException();
                // Validate every call before yielding the first: a bad second call cannot execute the first.
                var complete = ImmutableArray.CreateBuilder<ToolCall>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (index, parts) in calls)
                {
                    if (index != complete.Count || string.IsNullOrWhiteSpace(parts.Id) || parts.Id.Length > 128 ||
                        !ids.Add(parts.Id) || parts.Type != "function" || parts.Name.Length is < 1 or > 128 ||
                        parts.Name.ToString().Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
                        throw new ProviderProtocolException();
                    JsonElement arguments;
                    try
                    {
                        using var doc = JsonDocument.Parse(parts.Arguments.ToString(), new() { MaxDepth = 32 });
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ProviderProtocolException();
                        arguments = doc.RootElement.Clone();
                    }
                    catch (JsonException) { throw new ProviderProtocolException(); }
                    complete.Add(new(parts.Id, parts.Name.ToString(), arguments));
                }
                foreach (var call in complete) yield return new ProviderEvent.Call(call);
                yield break;
            }
            using var chunk = ParseChunk(frame);
            var root = chunk.RootElement;
            if (root.TryGetProperty("error", out _)) throw new ProviderProtocolException();
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                throw new ProviderProtocolException();
            if (choices.GetArrayLength() == 0) continue; // Optional usage frame.
            if (choices.GetArrayLength() != 1 || finish is not null) throw new ProviderProtocolException();
            var choice = choices[0];
            if (!choice.TryGetProperty("index", out var choiceIndex) || !choiceIndex.TryGetInt32(out var ci) || ci != 0 ||
                !choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                throw new ProviderProtocolException();
            if (delta.TryGetProperty("content", out var text) && text.ValueKind != JsonValueKind.Null)
            {
                if (text.ValueKind != JsonValueKind.String) throw new ProviderProtocolException();
                yield return new ProviderEvent.Text(text.GetString()!);
            }
            if (delta.TryGetProperty("tool_calls", out var deltas))
            {
                if (deltas.ValueKind != JsonValueKind.Array) throw new ProviderProtocolException();
                foreach (var item in deltas.EnumerateArray())
                {
                    if (!item.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out var index) || index is < 0 or > 7)
                        throw new ProviderProtocolException();
                    if (!calls.TryGetValue(index, out var parts)) calls[index] = parts = new();
                    SetOnce(item, "id", ref parts.Id);
                    SetOnce(item, "type", ref parts.Type);
                    if (item.TryGetProperty("function", out var function))
                    {
                        if (function.ValueKind != JsonValueKind.Object) throw new ProviderProtocolException();
                        Append(function, "name", parts.Name, 128);
                        Append(function, "arguments", parts.Arguments, 32_000);
                    }
                }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                if (reason.ValueKind != JsonValueKind.String || reason.GetString() is not ("stop" or "tool_calls"))
                    throw new ProviderProtocolException();
                finish = reason.GetString();
            }
        }
        throw new ProviderProtocolException(); // EOF without [DONE] never dispatches tools.
    }

    private static JsonDocument ParseChunk(string frame)
    {
        try
        {
            var doc = JsonDocument.Parse(frame, new() { MaxDepth = 32 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        throw new ProviderProtocolException();
    }

    private static void SetOnce(JsonElement item, string property, ref string? value)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null) return;
        if (element.ValueKind != JsonValueKind.String || value is not null) throw new ProviderProtocolException();
        value = element.GetString();
        if (value?.Length > 128) throw new ProviderProtocolException();
    }

    private static void Append(JsonElement item, string property, StringBuilder value, int max)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null) return;
        if (element.ValueKind != JsonValueKind.String) throw new ProviderProtocolException();
        if (value.Length + element.GetString()!.Length > max) throw new ProviderProtocolException();
        value.Append(element.GetString());
    }

    private static async IAsyncEnumerable<string> FramesAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var total = 0;
        var first = true;
        bool priorCr = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) yield break;
            total += count;
            if (total > 2_000_000) throw new ProviderProtocolException();
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = buffer[i];
                if (first) { first = false; if (c == '\uFEFF') continue; }
                if (c == '\n' && priorCr) { priorCr = false; continue; }
                priorCr = c == '\r';
                if (c is '\r' or '\n')
                {
                    if (line.Length == 0)
                    {
                        if (data.Length > 0) { var frame = data.ToString(0, data.Length - 1); data.Clear(); yield return frame; }
                    }
                    else
                    {
                        var value = line.ToString();
                        if (value.StartsWith("data:", StringComparison.Ordinal))
                        {
                            var field = value[5..];
                            if (field.StartsWith(' ')) field = field[1..];
                            if (data.Length + field.Length > 64_000) throw new ProviderProtocolException();
                            data.Append(field).Append('\n');
                        }
                        line.Clear();
                    }
                }
                else { if (line.Length >= 64_000) throw new ProviderProtocolException(); line.Append(c); }
            }
        }
    }
}
