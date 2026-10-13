using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public enum SettingsLoadStatus { Missing, Loaded, Migrated, Invalid, Unsupported, Unavailable }
public sealed record SettingsLoadResult(AssistantSettings Settings, SettingsLoadStatus Status);

public interface IAssistantSettingsStore
{
    SettingsLoadResult Load();
    void Save(AssistantSettings settings);
}

// Fixed own namespace supplied by the host; no source/model/provider-controlled paths.
public sealed class FileAssistantSettingsStore : IAssistantSettingsStore
{
    private const int MaximumBytes = 16_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string folder;
    private readonly string file;
    private readonly object sync = new();

    public FileAssistantSettingsStore(string hostSettingsFolder)
    {
        folder = Path.GetFullPath(hostSettingsFolder);
        if (!Path.IsPathFullyQualified(hostSettingsFolder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("設定目錄必須為宿主指定的本機絕對路徑。");
        file = Path.Combine(folder, "settings.json");
    }

    public SettingsLoadResult Load()
    {
        lock (sync)
        {
            try
            {
                CheckPath();
                if (!File.Exists(file)) return new(AssistantSettings.Default, SettingsLoadStatus.Missing);
                var json = ReadBounded();
                using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Version", out var value) || value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetInt32(out var version))
                    return new(AssistantSettings.Default, SettingsLoadStatus.Invalid);
                if (version == 1)
                {
                    var legacy = StrictJson.Parse<LegacySettings>(json, MaximumBytes, 8);
                    var profile = legacy.AiProvider is null ? null : ProviderProfiles.Validate(legacy.AiProvider);
                    var migrated = AssistantSettings.Default with { AiProvider = profile };
                    migrated.Validate();
                    return new(migrated, SettingsLoadStatus.Migrated); // Do not rewrite the original.
                }
                if (version != 2) return new(AssistantSettings.Default, SettingsLoadStatus.Unsupported);
                var settings = StrictJson.Parse<AssistantSettings>(json, MaximumBytes, 8);
                settings.Validate(); return new(settings, SettingsLoadStatus.Loaded);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            { return new(AssistantSettings.Default, SettingsLoadStatus.Invalid); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(AssistantSettings.Default, SettingsLoadStatus.Unavailable); }
        }
    }

    public void Save(AssistantSettings settings)
    {
        settings.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("設定檔超過大小限制。");
        lock (sync)
        {
            CheckPath();
            if (Load().Status is SettingsLoadStatus.Unsupported or SettingsLoadStatus.Invalid or SettingsLoadStatus.Unavailable)
                throw new InvalidDataException("不覆寫未知、無效或無法驗證的設定檔。");
            Directory.CreateDirectory(folder); CheckPath();
            var temporary = Path.Combine(folder, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            var created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = true; stream.Write(bytes); stream.Flush(true); }
                CheckPath();
                File.Move(temporary, file, overwrite: true);
            }
            finally
            {
                // Only the exact file successfully created by this call can be removed.
                if (created && File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private string ReadBounded()
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("設定檔超過大小限制。");
        using var output = new MemoryStream();
        var buffer = new byte[2_048]; int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("設定檔超過大小限制。");
            output.Write(buffer, 0, count);
        }
        return Utf8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }

    private void CheckPath()
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("設定路徑不接受連結或重新解析點。");
        if (new FileInfo(file).LinkTarget is not null || File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("設定檔不接受連結或重新解析點。");
    }

    private sealed record LegacySettings([property: JsonRequired] int Version,
        [property: JsonRequired] ProviderIdentity? AiProvider);
}
