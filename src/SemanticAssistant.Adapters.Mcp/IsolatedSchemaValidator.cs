using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SemanticAssistant.Adapters.Mcp;

public sealed class SchemaValidationException(string code) : Exception("本機Schema驗證失敗；沒有傳送外部工具呼叫。")
{
    public string Code { get; } = code;
}

public sealed class IsolatedSchemaValidator
{
    private readonly string worker;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim slots = new(1);
    public int? LastProcessId { get; private set; } // Own helper only; schema/arguments are never diagnostic output.
    public TimeSpan? LastCpuTime { get; private set; }
    public TimeSpan? LastUserCpuTime { get; private set; }
    public IsolatedSchemaValidator(string? workerPath = null, TimeSpan? deadline = null)
    {
        worker = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "SemanticAssistant.Cli.dll"));
        timeout = deadline ?? TimeSpan.FromSeconds(5);
        if (timeout < TimeSpan.FromMilliseconds(50) || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(deadline));
    }
    public async Task ValidateAsync(JsonElement schema, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); McpSchemaPolicy.CheckSchema(schema); McpSchemaPolicy.CheckArguments(arguments);
        await slots.WaitAsync(token);
        try { await RunAsync(schema, arguments, token).ConfigureAwait(false); }
        finally { slots.Release(); }
    }
    private async Task RunAsync(JsonElement schema, JsonElement arguments, CancellationToken token)
    {
        LastProcessId = null; LastCpuTime = null; LastUserCpuTime = null;
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(host) || !File.Exists(worker)) throw new SchemaValidationException("worker_unavailable");
        var nonce = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new { version = 1, nonce, schema, arguments });
        if (Encoding.UTF8.GetByteCount(request) > 64000) throw new SchemaValidationException("request_limit");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(worker)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        start.ArgumentList.Add(worker); start.ArgumentList.Add("--mcp-schema-worker-v1");
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        SchemaWorkerJob? job = null; Task? outputTask = null, errorsTask = null, inputTask = null, monitorTask = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new SchemaValidationException("worker_unavailable");
            LastProcessId = process.Id;
            job = SchemaWorkerJob.Attach(process); // Before sending any schema or arguments.
            var response = ReadOutputAsync(process.StandardOutput, deadline.Token); outputTask = response;
            errorsTask = DrainErrorsAsync(process.StandardError, deadline.Token);
            inputTask = SendAsync(process, request, deadline.Token);
            var resources = MonitorResourcesAsync(process, job, deadline.Token); monitorTask = resources;
            await Task.WhenAll(inputTask, process.WaitForExitAsync(deadline.Token), response, errorsTask, resources).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (await resources is { } resourceFailure) throw new SchemaValidationException(resourceFailure);
            if (process.ExitCode != 0) throw new SchemaValidationException(
                CpuLimitReached(process) ? "validation_cpu_limit" : "worker_failed");
            using var document = JsonDocument.Parse(await response, new() { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                root.GetProperty("version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != nonce ||
                root.GetProperty("valid").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new SchemaValidationException("worker_protocol");
            if (!root.GetProperty("valid").GetBoolean()) throw new SchemaValidationException("invalid_arguments");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new SchemaValidationException("validation_timeout"); }
        catch (SchemaValidationException) { throw; }
        catch { throw new SchemaValidationException(
            CpuLimitReached(process) ? "validation_cpu_limit" : "worker_failed"); }
        finally
        {
            deadline.Cancel();
            if (LastProcessId is not null)
            {
                try { LastCpuTime = process.TotalProcessorTime; } catch { }
                try { LastUserCpuTime = process.UserProcessorTime; } catch { }
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                job?.Dispose(); job = null; // Windows kill-on-close also handles failed/uncertain Kill.
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            }
            job?.Dispose();
            // Observe all pipe tasks; no late validation result can issue an approval.
            var cleanup = ObserveAsync(new[] { outputTask, errorsTask, inputTask, monitorTask }.OfType<Task>());
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }
    }
    private static async Task ObserveAsync(IEnumerable<Task> tasks)
    { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { } }
    private static async Task<string?> MonitorResourcesAsync(Process process, SchemaWorkerJob? job, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(50, token).ConfigureAwait(false); process.Refresh();
            // The Windows Job may terminate the helper before this poll executes.
            // Its final CPU time remains available through our process handle.
            var failure = CpuLimitReached(process) ? "validation_cpu_limit" : null;
            if (failure is null && process.HasExited) return null;
            if (failure is null && process.PrivateMemorySize64 > 256L * 1024 * 1024)
                failure = "validation_memory_limit";
            if (failure is null) continue;
            // Job CPU checks are periodic, so also enforce a host-observed total CPU budget.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            job?.Dispose(); return failure;
        }
    }
    private static bool CpuLimitReached(Process process)
    {
        try { return process.TotalProcessorTime >= TimeSpan.FromSeconds(2); }
        catch { return false; } // An unavailable measurement cannot authorize a worker reply.
    }
    private static async Task SendAsync(Process process, string request, CancellationToken token)
    {
        await process.StandardInput.WriteAsync(request.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false); process.StandardInput.Close();
    }
    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[128];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > 512) throw new SchemaValidationException("worker_protocol");
            result.Append(buffer, 0, count);
        }
    }
    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[256];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
    }
}
