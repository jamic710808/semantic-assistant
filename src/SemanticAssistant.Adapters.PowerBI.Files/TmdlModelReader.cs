using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.PowerBI.Files;

// Offline metadata only: never creates a Server, connection, query or writer.
public sealed class TmdlModelReader : ISemanticModelReader
{
    private const int MaxFiles = 512;
    private const long MaxFileBytes = 1_048_576;
    private const long MaxTotalBytes = 8_388_608;
    private readonly string folder;

    public TmdlModelReader(string explicitlySelectedFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explicitlySelectedFolder);
        folder = Path.GetFullPath(explicitlySelectedFolder);
    }

    public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private ModelSnapshot Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("選取的 TMDL 目錄不存在或無法存取。");
        var before = Fingerprint(cancellationToken);
        // Official TOM deserializer reads files into memory; does not connect to Power BI.
        using var database = TmdlSerializer.DeserializeDatabaseFromFolder(folder);
        cancellationToken.ThrowIfCancellationRequested();
        var after = Fingerprint(cancellationToken);
        if (before != after) throw new IOException("TMDL 檔案在讀取期間改變。");
        if (database.Model is null) throw new InvalidDataException("TMDL 未包含 model。");
        var tables = database.Model.Tables.Select(t => new TableDefinition(t.Name,
            t.Columns.Select(c => new ColumnDefinition(c.Name, c.DataType.ToString(), c is CalculatedColumn cc ? cc.Expression : null)).ToImmutableArray(),
            t.Measures.Select(m => new MeasureDefinition(m.Name, m.Expression ?? "")).ToImmutableArray())).ToImmutableArray();
        var modelId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder))).ToLowerInvariant();
        return new(new("tmdl-" + modelId, database.Name ?? "TMDL 模型", before, false), tables)
        {
            Relationships = database.Model.Relationships.OfType<SingleColumnRelationship>()
                .Select(r => new RelationshipDefinition(r.Name, r.FromColumn.Table.Name, r.FromColumn.Name,
                    r.ToColumn.Table.Name, r.ToColumn.Name, r.IsActive, r.FromCardinality.ToString(), r.ToCardinality.ToString(),
                    r.CrossFilteringBehavior.ToString(), r.SecurityFilteringBehavior.ToString())).ToImmutableArray(),
            UnsupportedRelationshipNames = database.Model.Relationships.Where(r => r is not SingleColumnRelationship)
                .Select(r => r.Name).ToImmutableArray()
        };
    }

    private string Fingerprint(CancellationToken cancellationToken)
    {
        // Reject reparse points before traversing. Do not follow a link outside the selected folder.
        var pending = new Queue<string>();
        pending.Enqueue(folder);
        var files = new List<string>();
        var visited = 0;
        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 512) throw new InvalidDataException("TMDL 目錄數超過限制。");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("TMDL reader 不接受連結目錄。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("TMDL reader 不接受連結檔案。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Enqueue(entry);
                else if (Path.GetExtension(entry).Equals(".tmdl", StringComparison.OrdinalIgnoreCase)) files.Add(entry);
                if (files.Count > MaxFiles) throw new InvalidDataException("TMDL 檔案數超過限制。");
            }
        }
        if (files.Count == 0) throw new InvalidDataException("選取目錄內沒有 TMDL 檔案。");
        files.Sort(StringComparer.Ordinal);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(file).Length;
            if (length > MaxFileBytes || (total += length) > MaxTotalBytes)
                throw new InvalidDataException("TMDL 內容超過大小限制。");
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length != length) throw new IOException("TMDL 檔案大小在讀取期間改變。");
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/'));
            digest.AppendData(BitConverter.GetBytes(name.Length));
            digest.AppendData(name);
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
}
