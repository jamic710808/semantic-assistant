using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace SemanticAssistant.Application;

public sealed record WriteAuditEntry(Guid? OperationId, string Status, string? LastCode, int? Objects, bool? IsSynthetic)
{
    public bool RequiresReview => Status != "recorded_verified" && Status != "recorded_rolled_back";
}
public sealed record WriteAuditReview(ImmutableArray<WriteAuditEntry> Entries)
{
    public bool ModelStateVerified => false;
    public bool ModelMutationPerformed => false;
    public bool AuthorizationsRestored => false;
}

// Read-only restart triage. Audit codes are observations, never authority or backups.
public static class ModelWriteAuditReader
{
    public static WriteAuditReview Read(string explicitlySelectedFolder, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(explicitlySelectedFolder);
        EnsurePath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("稽核目錄不存在。");
        var files = Directory.EnumerateFiles(root, "operation-*.jsonl").Take(1001).ToArray();
        if (files.Length > 1000) throw new InvalidDataException("稽核檔案數超過上限。");
        var entries = ImmutableArray.CreateBuilder<WriteAuditEntry>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(file)["operation-".Length..];
            Guid? operation = Guid.TryParseExact(stem, "N", out var id) && id != Guid.Empty ? id : null;
            try { entries.Add(ReadOne(file, operation, token)); }
            catch (OperationCanceledException) { throw; }
            catch { entries.Add(new(operation, "unreadable", null, null, null)); }
        }
        EnsurePath(root); token.ThrowIfCancellationRequested();
        return new(entries.ToImmutable());
    }

    private static WriteAuditEntry ReadOne(string file, Guid? operation, CancellationToken token)
    {
        if (operation is null) throw new InvalidDataException();
        EnsurePath(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int maxBytes = 20_000;
        var length = stream.Length;
        if (length is < 1 or > maxBytes) throw new InvalidDataException();
        var buffer = new byte[maxBytes + 1]; var count = 0;
        while (count < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break; count += read;
        }
        if (count != length || stream.Length != length || buffer[count - 1] != (byte)'\n') throw new InvalidDataException();
        EnsurePath(file);
        var lines = new UTF8Encoding(false, true).GetString(buffer, 0, count).Split('\n');
        if (lines.Length is < 2 or > 65) throw new InvalidDataException();
        string? state = null; string? identity = null; int objects = 0; bool synthetic = false;
        foreach (var line in lines[..^1])
        {
            token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(line, new() { MaxDepth = 2 });
            var entry = json.RootElement;
            string[] keys = ["Version", "At", "OperationId", "PlanHash", "SourceHash", "DefinitionRevisionHash", "IsSynthetic", "Objects", "Code"];
            var properties = entry.EnumerateObject().ToArray();
            if (properties.Length != keys.Length || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != keys.Length ||
                properties.Any(p => !keys.Contains(p.Name, StringComparer.Ordinal)) || entry.GetProperty("Version").GetInt32() != 1 ||
                entry.GetProperty("OperationId").GetGuid() != operation || !entry.GetProperty("At").TryGetDateTimeOffset(out _)) throw new InvalidDataException();
            var hashes = keys.Skip(3).Take(3).Select(k => entry.GetProperty(k).GetString()).ToArray();
            if (hashes.Any(h => h is null || h.Length != 64 || h.Any(c => !char.IsAsciiHexDigit(c)))) throw new InvalidDataException();
            var currentObjects = entry.GetProperty("Objects").GetInt32();
            var currentSynthetic = entry.GetProperty("IsSynthetic").GetBoolean();
            if (currentObjects is < 1 or > 16) throw new InvalidDataException();
            var binding = string.Join(":", hashes) + ":" + currentObjects + ":" + currentSynthetic;
            if (identity is not null && identity != binding) throw new InvalidDataException();
            identity = binding; objects = currentObjects; synthetic = currentSynthetic;
            var code = entry.GetProperty("Code").GetString();
            var valid = (state, code) switch
            {
                (null, "intent") => true,
                ("intent", "verified" or "rollback_started") => true,
                ("rollback_started", "failed_rolled_back" or "partial_failure") => true,
                _ => false
            };
            if (!valid) throw new InvalidDataException();
            state = code;
        }
        var status = state switch
        {
            "verified" => "recorded_verified", "failed_rolled_back" => "recorded_rolled_back",
            "partial_failure" => "recorded_partial_failure", _ => "unresolved"
        };
        return new(operation, status, state, objects, synthetic);
    }

    private static void EnsurePath(string path)
    {
        for (FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); item is not null;)
        {
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("不接受重新解析點。");
            item = item switch { FileInfo f => f.Directory, DirectoryInfo d => d.Parent, _ => null };
        }
    }
}
