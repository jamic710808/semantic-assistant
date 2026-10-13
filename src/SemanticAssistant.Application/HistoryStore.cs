using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SemanticAssistant.Application;

public enum HistoryOutcome { Success, Denied, Cancelled, Failed }
public enum HistoryLoadStatus { Missing, Loaded, Invalid, Unsupported, Unavailable }
public sealed record HistoryPolicy([property: JsonRequired] bool SaveSummaries = false,
    [property: JsonRequired] bool SaveArtifacts = false, [property: JsonRequired] int RetentionDays = 7,
    [property: JsonRequired] int MaximumEntries = 50)
{
    public void Validate()
    {
        if (RetentionDays is < 1 or > 30 || MaximumEntries is < 1 or > 100)
            throw new InvalidDataException("歷史保存期限或筆數超出範圍。");
    }
}

// No message, model name/identity, DAX, rows, provider, credential or permission fields.
public sealed record HistoryEntry([property: JsonRequired] Guid Id, [property: JsonRequired] DateTimeOffset CreatedAt,
    [property: JsonRequired] AssistantMode Mode, [property: JsonRequired] ModelSourceKind Source,
    [property: JsonRequired] HistoryOutcome Outcome, [property: JsonRequired] int ToolCalls,
    [property: JsonRequired] string? ArtifactFileName);
public sealed record HistorySnapshot([property: JsonRequired] int Version, [property: JsonRequired] HistoryPolicy Policy,
    [property: JsonRequired] ImmutableArray<HistoryEntry> Entries)
{
    public static HistorySnapshot Empty => new(1, new(), []);
}
public sealed record HistoryLoadResult(HistorySnapshot Snapshot, HistoryLoadStatus Status);

public interface IHistoryStore
{
    HistoryLoadResult Load();
    void SetPolicy(HistoryPolicy policy);
    void RecordTurn(AssistantMode mode, ModelSourceKind source, HistoryOutcome outcome, int toolCalls);
    void RecordArtifact(AssistantMode mode, ModelSourceKind source, string hostArtifactPath);
    void Remove(Guid id);
    void Clear();
    string ArtifactLocation(HistoryEntry entry);
}

public sealed class FileHistoryStore : IHistoryStore
{
    private const int MaximumBytes = 128_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    private static readonly Regex ArtifactName = new(@"\Areport-[0-9]{8}-[0-9]{6}-[a-f0-9]{32}\.html\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly string folder, file, lockFile, artifactFolder;
    private readonly TimeProvider clock;
    private readonly object sync = new();

    public FileHistoryStore(string hostHistoryFolder, string hostArtifactFolder, TimeProvider? clock = null)
    {
        folder = LocalFolder(hostHistoryFolder); artifactFolder = LocalFolder(hostArtifactFolder);
        file = Path.Combine(folder, "history.json"); lockFile = Path.Combine(folder, "history.lock");
        this.clock = clock ?? TimeProvider.System;
    }

    public HistoryLoadResult Load()
    {
        lock (sync)
        {
            try
            {
                CheckHistoryPath();
                if (!File.Exists(file)) return new(HistorySnapshot.Empty, HistoryLoadStatus.Missing);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > MaximumBytes) throw new InvalidDataException();
                using var buffer = new MemoryStream(); var bytes = new byte[2048]; int count;
                while ((count = stream.Read(bytes)) > 0)
                {
                    if (buffer.Length + count > MaximumBytes) throw new InvalidDataException();
                    buffer.Write(bytes, 0, count);
                }
                var json = Utf8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
                using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number))
                    throw new InvalidDataException();
                if (number != 1) return new(HistorySnapshot.Empty, HistoryLoadStatus.Unsupported);
                var snapshot = StrictJson.Parse<HistorySnapshot>(json, MaximumBytes, 8);
                Validate(snapshot);
                return new(Prune(snapshot), HistoryLoadStatus.Loaded);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
            { return new(HistorySnapshot.Empty, HistoryLoadStatus.Invalid); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(HistorySnapshot.Empty, HistoryLoadStatus.Unavailable); }
        }
    }

    public void SetPolicy(HistoryPolicy policy)
    { policy.Validate(); Mutate(s => s with { Policy = policy }); }
    public void Clear() => Mutate(s => s with { Entries = [] });
    public void Remove(Guid id) => Mutate(s => s with { Entries = s.Entries.Where(e => e.Id != id).ToImmutableArray() });

    public void RecordTurn(AssistantMode mode, ModelSourceKind source, HistoryOutcome outcome, int toolCalls)
    {
        var entry = new HistoryEntry(Guid.NewGuid(), clock.GetUtcNow(), mode, source, outcome, toolCalls, null);
        ValidateEntry(entry);
        // Disabled recording is read-only and does not create a directory or lock file.
        if (!RequireReadable().Policy.SaveSummaries) return;
        Mutate(s => s.Policy.SaveSummaries ? s with { Entries = s.Entries.Add(entry) } : s);
    }

    public void RecordArtifact(AssistantMode mode, ModelSourceKind source, string hostArtifactPath)
    {
        if (!RequireReadable().Policy.SaveArtifacts) return;
        var path = Path.GetFullPath(hostArtifactPath);
        if (!Path.IsPathFullyQualified(hostArtifactPath) || !string.Equals(Path.GetDirectoryName(path), artifactFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只接受宿主報告目錄的索引。");
        var entry = new HistoryEntry(Guid.NewGuid(), clock.GetUtcNow(), mode, source, HistoryOutcome.Success, 0, Path.GetFileName(path));
        ValidateEntry(entry); CheckPath(artifactFolder, path);
        if (!File.Exists(path)) throw new InvalidDataException("報告檔案不存在。");
        Mutate(s => s.Policy.SaveArtifacts ? s with { Entries = s.Entries.Add(entry) } : s);
    }

    // Display-only location. No file execution, source connection, query or authority restoration.
    public string ArtifactLocation(HistoryEntry entry)
    {
        ValidateEntry(entry);
        if (entry.ArtifactFileName is null) throw new InvalidDataException("此紀錄沒有報告索引。");
        var path = Path.Combine(artifactFolder, entry.ArtifactFileName); CheckPath(artifactFolder, path); return path;
    }

    private void Mutate(Func<HistorySnapshot, HistorySnapshot> mutation)
    {
        lock (sync)
        {
            CheckHistoryPath(); RequireReadable(); Directory.CreateDirectory(folder); CheckHistoryPath();
            // FileShare.None coordinates independent windows/processes; fail without overwriting on contention.
            using var exclusive = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CheckHistoryPath(); var next = Prune(mutation(RequireReadable())); Validate(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("歷史紀錄超過大小限制。");
            var temporary = Path.Combine(folder, "history-" + Guid.NewGuid().ToString("N") + ".tmp"); var created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = true; stream.Write(bytes); stream.Flush(true); }
                CheckHistoryPath(); File.Move(temporary, file, overwrite: true);
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private HistorySnapshot RequireReadable()
    {
        var loaded = Load();
        if (loaded.Status is HistoryLoadStatus.Invalid or HistoryLoadStatus.Unsupported or HistoryLoadStatus.Unavailable)
            throw new InvalidDataException("不覆寫未知、無效或無法驗證的歷史紀錄。");
        return loaded.Snapshot;
    }
    private HistorySnapshot Prune(HistorySnapshot snapshot) => snapshot with
    {
        Entries = snapshot.Entries.Where(e => e.CreatedAt > clock.GetUtcNow().AddDays(-snapshot.Policy.RetentionDays) &&
            (e.ArtifactFileName is null ? snapshot.Policy.SaveSummaries : snapshot.Policy.SaveArtifacts))
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id).Take(snapshot.Policy.MaximumEntries).ToImmutableArray()
    };
    private void Validate(HistorySnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Policy is null || snapshot.Entries.IsDefault || snapshot.Entries.Length > 100)
            throw new InvalidDataException("歷史格式無效。");
        snapshot.Policy.Validate(); var ids = new HashSet<Guid>();
        foreach (var entry in snapshot.Entries)
        { if (entry is null || !ids.Add(entry.Id)) throw new InvalidDataException(); ValidateEntry(entry); }
    }
    private void ValidateEntry(HistoryEntry entry)
    {
        if (entry.Id == Guid.Empty || !Enum.IsDefined(entry.Mode) || !Enum.IsDefined(entry.Source) || !Enum.IsDefined(entry.Outcome) ||
            entry.ToolCalls is < 0 or > 8 || entry.CreatedAt == default || entry.CreatedAt.Offset != TimeSpan.Zero ||
            entry.CreatedAt > clock.GetUtcNow().AddMinutes(1) ||
            entry.ArtifactFileName is { } name && (name.Length > 100 || !ArtifactName.IsMatch(name) || entry.ToolCalls != 0 || entry.Outcome != HistoryOutcome.Success))
            throw new InvalidDataException("歷史紀錄欄位無效。");
    }
    private void CheckHistoryPath() { CheckPath(folder, file); CheckPath(folder, lockFile); }
    private static string LocalFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("歷史路徑必須是本機絕對目錄。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static void CheckPath(string folder, string file)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("路徑不接受連結。");
        var info = new FileInfo(file);
        if (info.LinkTarget is not null || info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("檔案不接受連結。");
    }
}
