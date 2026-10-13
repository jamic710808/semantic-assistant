using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.Mcp;

// The host must obtain user confirmation of the executable/arguments before calling StartAsync.
public sealed class McpStdioClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Guid clientId = Guid.NewGuid();
    private readonly string source;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeLock = new(1);
    private readonly SemaphoreSlim callLock = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task pump, stderr;
    private readonly IsolatedSchemaValidator validator = new();
    private readonly ConcurrentDictionary<string, byte> validations = new();
    private ImmutableDictionary<string, McpToolDescriptor> catalog = ImmutableDictionary<string, McpToolDescriptor>.Empty;
    private long catalogVersion = 1;
    private volatile bool dirty = true;
    private int disposed;
    public string Source => source;
    public int ProcessId { get; }
    public bool IsAlive => !lifetime.IsCancellationRequested && !process.HasExited;

    private McpStdioClient(Process process, McpProcessConfiguration configuration)
    {
        this.process = process;
        ProcessId = process.Id;
        source = $"{configuration.Executable} · 工作目錄 {configuration.WorkingDirectory} · PID {process.Id} · {configuration.Fingerprint[..16]} · {clientId:N}";
        pump = ReadLoopAsync(); stderr = DrainStderrAsync();
    }

    public static async Task<McpStdioClient> StartAsync(McpProcessConfiguration configuration, CancellationToken token)
    {
        configuration.Validate(); token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(configuration.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            WorkingDirectory = configuration.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        foreach (var argument in configuration.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH" })
            if (System.Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var pair in configuration.Environment) start.Environment[pair.Key] = pair.Value;
        var process = Process.Start(start) ?? throw new McpException();
        var client = new McpStdioClient(process, configuration);
        try
        {
            var result = await client.RequestAsync("initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "SemanticAssistant", version = "0.1.0" }
            }, token);
            var version = result.GetProperty("protocolVersion").GetString();
            if (version is not ("2025-11-25" or "2025-06-18" or "2024-11-05") ||
                !result.GetProperty("capabilities").TryGetProperty("tools", out _)) throw new McpException();
            await client.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, token);
            return client;
        }
        catch { await client.DisposeAsync(); throw new McpException(); }
    }

    public async Task<ImmutableArray<McpToolDescriptor>> ListToolsAsync(CancellationToken token)
    {
        await callLock.WaitAsync(token);
        try
        {
            var version = Interlocked.Increment(ref catalogVersion);
            var tools = ImmutableDictionary.CreateBuilder<string, McpToolDescriptor>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var result = await RequestAsync("tools/list", cursor is null ? new { } : (object)new { cursor }, token);
                foreach (var tool in result.GetProperty("tools").EnumerateArray())
                {
                    var name = tool.GetProperty("name").GetString()!;
                    var schema = tool.GetProperty("inputSchema").Clone();
                    if (tools.Count >= 128 || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
                        name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) || tools.ContainsKey(name) ||
                        schema.ValueKind != JsonValueKind.Object || schema.GetRawText().Length > 16000) throw new McpException();
                    var description = tool.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                    if (description.Length > 4000) throw new McpException();
                    McpSchemaPolicy.CheckSchema(schema);
                    var hash = Hash(tool.GetRawText());
                    tools[name] = new(source, version, name, description, schema, hash);
                }
                cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                if (cursor is not null && (cursor.Length > 1024 || !cursors.Add(cursor))) throw new McpException();
            } while (cursor is not null);
            if (catalogVersion != version) throw new McpException();
            catalog = tools.ToImmutable(); validations.Clear(); dirty = false;
            return catalog.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToImmutableArray();
        }
        finally { callLock.Release(); }
    }

    public async Task ValidateForPreviewAsync(McpToolDescriptor tool, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureToolCurrent(tool); McpSchemaPolicy.CheckArguments(arguments);
        var key = ValidationKey(tool, arguments);
        if (!validations.ContainsKey(key))
        {
            using var validationLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try { await validator.ValidateAsync(tool.InputSchema, arguments, validationLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { throw new McpException(); }
            token.ThrowIfCancellationRequested(); EnsureToolCurrent(tool);
            if (validations.Count >= 256) validations.Clear();
            validations[key] = 0;
        }
        token.ThrowIfCancellationRequested(); EnsureToolCurrent(tool);
    }
    private static string ValidationKey(McpToolDescriptor tool, JsonElement arguments) =>
        tool.CatalogVersion + ":" + tool.DefinitionHash + ":" + Hash(arguments.GetRawText());
    public async Task<McpInvocationApproval> ApproveAfterUserConfirmationAsync(McpToolDescriptor tool, JsonElement arguments,
        PermissionSession session, PermissionSnapshot permissions, CancellationToken token)
    {
        EnsureToolCurrent(tool); EnsurePermission(session, permissions);
        await ValidateForPreviewAsync(tool, arguments, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); EnsureToolCurrent(tool); EnsurePermission(session, permissions);
        return new(clientId, tool, Hash(arguments.GetRawText()), permissions, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    public async Task<McpInvocationResult> CallToolAsync(McpToolDescriptor tool, JsonElement arguments,
        McpInvocationApproval? approval, PermissionSession session, PermissionSnapshot permissions, CancellationToken token)
    {
        await callLock.WaitAsync(token);
        try
        {
            EnsureToolCurrent(tool); EnsurePermission(session, permissions); McpSchemaPolicy.CheckArguments(arguments);
            if (approval is null || approval.Client != clientId || approval.Tool != tool || approval.Permissions != permissions ||
                approval.ExpiresAt <= DateTimeOffset.UtcNow || approval.ArgumentsHash != Hash(arguments.GetRawText()) ||
                Interlocked.Exchange(ref approval.Used, 1) != 0) throw new McpException();
            token.ThrowIfCancellationRequested(); EnsureToolCurrent(tool); EnsurePermission(session, permissions);
            var result = await RequestAsync("tools/call", new { name = tool.Name, arguments }, token);
            EnsureToolCurrent(tool); EnsurePermission(session, permissions); token.ThrowIfCancellationRequested();
            var content = result.GetProperty("content").Clone();
            if (content.ValueKind != JsonValueKind.Array) throw new McpException();
            // Render only text locally; never follow resource links or execute returned HTML/instructions.
            return new(tool.Name, result.TryGetProperty("isError", out var error) && error.GetBoolean(), content,
                result.TryGetProperty("structuredContent", out var structured) ? structured.Clone() : null);
        }
        finally { callLock.Release(); }
    }

    private void EnsureToolCurrent(McpToolDescriptor tool)
    {
        if (!IsAlive || dirty || tool.Source != source || tool.CatalogVersion != catalogVersion ||
            !catalog.TryGetValue(tool.Name, out var current) || current != tool) throw new McpException();
    }
    public bool IsToolCurrent(McpToolDescriptor tool)
    { try { EnsureToolCurrent(tool); return true; } catch { return false; } }
    private static void EnsurePermission(PermissionSession session, PermissionSnapshot permissions)
    { if (!session.IsCurrent(permissions) || !permissions.Allows(Capability.ExternalTool)) throw new McpException(); }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { if (!names.Add(field.Name)) throw new McpException(); CheckDuplicates(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new McpException();
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch { Stop(); throw new McpException(); } // No automatic retries after an uncertain external operation.
        finally { pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value, CancellationToken token)
    {
        var message = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(message) > 64000) throw new McpException();
        await writeLock.WaitAsync(token);
        try { await process.StandardInput.WriteLineAsync(message.AsMemory(), token); await process.StandardInput.FlushAsync(token); }
        finally { writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var buffer = new char[4096]; var line = new StringBuilder(); var messages = 0;
            while (!lifetime.IsCancellationRequested)
            {
                var count = await process.StandardOutput.ReadAsync(buffer, lifetime.Token);
                if (count == 0) throw new McpException();
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\n') { if (line.Length >= 64000) throw new McpException(); line.Append(buffer[i]); continue; }
                    if (++messages > 4096) throw new McpException();
                    if (Encoding.UTF8.GetByteCount(line.ToString()) > 64000) throw new McpException();
                    using var doc = JsonDocument.Parse(line.ToString(), new() { MaxDepth = 32 }); line.Clear();
                    var root = doc.RootElement; CheckDuplicates(root);
                    if (root.GetProperty("jsonrpc").GetString() != "2.0") throw new McpException();
                    if (root.TryGetProperty("method", out var method))
                    {
                        if (root.TryGetProperty("id", out var serverId))
                            await WriteAsync(new { jsonrpc = "2.0", id = serverId.Clone(), error = new { code = -32601, message = "Client capability not supported" } }, lifetime.Token);
                        else if (method.GetString() == "notifications/tools/list_changed")
                        { dirty = true; Interlocked.Increment(ref catalogVersion); }
                        continue;
                    }
                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                        !pending.TryRemove(id.GetString()!, out var completion)) throw new McpException();
                    if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result)) completion.TrySetException(new McpException());
                    else completion.TrySetResult(result.Clone());
                }
            }
        }
        catch { Stop(); }
    }
    private async Task DrainStderrAsync()
    {
        try { var buffer = new char[4096]; while (await process.StandardError.ReadAsync(buffer, lifetime.Token) > 0) { } }
        catch { /* Do not persist or expose process logs; they may contain credentials. */ }
    }
    private void Stop()
    {
        lifetime.Cancel(); dirty = true; validations.Clear();
        foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(new McpException());
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop();
        try { process.StandardInput.Close(); } catch { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
        try { await Task.WhenAll(pump, stderr).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        process.Dispose();
    }
}
