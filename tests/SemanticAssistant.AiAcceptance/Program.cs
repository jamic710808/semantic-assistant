using System.Text.Json;
using SemanticAssistant.Adapters.AI;

namespace SemanticAssistant.AiAcceptance;

public sealed record AiAcceptanceArguments(string ProfilePath, string OutputDirectory, bool Live,
    bool AllowMessage, bool AllowMetadata, bool AllowQueryResults)
{
    public static AiAcceptanceArguments Parse(string[] args)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "--profile" or "--output-dir")
            {
                if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal) ||
                    !values.TryAdd(name, args[index])) throw new ArgumentException();
            }
            else if (name is "--preflight" or "--live" or "--allow-message-egress" or
                     "--allow-metadata-egress" or "--allow-query-egress")
            {
                if (!flags.Add(name)) throw new ArgumentException();
            }
            else throw new ArgumentException();
        }
        if (!values.TryGetValue("--profile", out var profile) || !values.TryGetValue("--output-dir", out var output) ||
            flags.Contains("--preflight") == flags.Contains("--live")) throw new ArgumentException();
        var live = flags.Contains("--live");
        var message = flags.Contains("--allow-message-egress");
        var metadata = flags.Contains("--allow-metadata-egress");
        var query = flags.Contains("--allow-query-egress");
        if (live ? !(message && metadata && query) : message || metadata || query) throw new ArgumentException();
        return new(Path.GetFullPath(profile), Path.GetFullPath(output), live, message, metadata, query);
    }
}

public static class AcceptanceReportFile
{
    public static FileStream OpenNew(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || Directory.Exists(full) || File.Exists(full))
            throw new IOException();
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
        Directory.CreateDirectory(full);
        return new FileStream(Path.Combine(full, "verification.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = AiAcceptanceArguments.Parse(args);
            using var input = new FileStream(options.ProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 8192) throw new InvalidDataException();
            var bytes = new byte[8193];
            var count = await input.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > 8192) throw new InvalidDataException();
            var profile = AiAcceptanceProfile.Parse(new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count));
            if (options.Live && new Uri(profile.Provider.Endpoint).Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException();
            // Reserve a new output before reading any credential or sending an AI request.
            using var output = AcceptanceReportFile.OpenNew(options.OutputDirectory);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                var report = await new AiAcceptanceVerifier(profile, new WindowsCredentialVault()).RunAsync(options.Live,
                    options.AllowMessage, options.AllowMetadata, options.AllowQueryResults, cancellation.Token);
                await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
                output.Flush(flushToDisk: true);
                Console.WriteLine($"ai-acceptance;preflight={report.LocalPreflightPassed};live={report.ConfiguredEndpointVerified};synthetic-only=true");
                Console.WriteLine(Path.Combine(options.OutputDirectory, "verification.json"));
                return options.Live ? report.ConfiguredEndpointVerified ? 0 : 1 : report.LocalPreflightPassed ? 0 : 1;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (ArgumentException) { Console.Error.WriteLine("ai-acceptance-invalid-options-or-profile;no-secret-output=true"); return 2; }
        catch (OperationCanceledException) { Console.Error.WriteLine("ai-acceptance-cancelled;no-secret-output=true"); return 3; }
        catch { Console.Error.WriteLine("ai-acceptance-failed;no-secret-output=true"); return 1; }
    }
}
