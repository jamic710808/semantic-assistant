using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SemanticAssistant.Application;

public interface IModelWriteJournal
{
    IModelWriteAudit Begin(DefinitionPreview preview);
}
public interface IModelWriteAudit : IDisposable
{
    void Record(string code);
}

// An audit of operation state, not a backup of model definitions or data.
// CreateNew also prevents silently retrying an operation after restarting the host.
public sealed class FileModelWriteJournal(string folder) : IModelWriteJournal
{
    public IModelWriteAudit Begin(DefinitionPreview preview)
    {
        var root = Path.GetFullPath(folder);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("稽核目錄不可使用重新解析點。");
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFiles(root, "operation-*.jsonl").Take(1000).Count() >= 1000)
            throw new IOException("稽核操作紀錄已達上限；請先封存紀錄。");
        var path = Path.Combine(root, $"operation-{preview.Plan.OperationId:N}.jsonl");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            var audit = new Audit(stream, preview);
            audit.Record("intent"); return audit;
        }
        catch { stream.Dispose(); throw; }
    }
    private sealed class Audit(FileStream stream, DefinitionPreview preview) : IModelWriteAudit
    {
        public void Record(string code)
        {
            if (stream.Length > 16_000 || string.IsNullOrWhiteSpace(code) || code.Length > 64 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new IOException("稽核紀錄無效。");
            var entry = new
            {
                Version = 1,
                At = DateTimeOffset.UtcNow,
                preview.Plan.OperationId,
                preview.PlanHash,
                SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Plan.Target.Id))),
                DefinitionRevisionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Plan.ExpectedRevision))),
                preview.Plan.Target.IsSynthetic,
                Objects = DefinitionRules.ObjectCount(preview.Plan),
                Code = code
            };
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(entry)); stream.WriteByte((byte)'\n'); stream.Flush(flushToDisk: true);
        }
        public void Dispose() => stream.Dispose();
    }
}
