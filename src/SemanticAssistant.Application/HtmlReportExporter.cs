using System.Text;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

// The host supplies a fixed output folder; a provider cannot supply file paths.
public sealed class HtmlReportExporter(string hostOutputFolder, PermissionSession session, TimeProvider? resultClock = null,
    Action? modeGuard = null)
{
    private readonly string outputFolder = Path.GetFullPath(hostOutputFolder);

    public async Task<string> ExportAsync(QueryResult result, PermissionSnapshot permissions, CancellationToken cancellationToken,
        ReportSpec? spec = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        modeGuard?.Invoke();
        if (!session.IsCurrent(permissions) || !permissions.Allows(Capability.ArtifactWrite))
            throw new UnauthorizedAccessException("未授權本機檔案寫入。");
        if (permissions.Target != result.Source) throw new UnauthorizedAccessException("報告來源與授權目標不符。");
        QueryResultRetention.EnsureFresh(result, resultClock);
        var html = HtmlReportRenderer.Render(result, spec, resultClock);
        if (Encoding.UTF8.GetByteCount(html) > 256_000) throw new InvalidDataException("報告超過大小限制。");
        QueryResultRetention.EnsureFresh(result, resultClock);
        Directory.CreateDirectory(outputFolder);
        modeGuard?.Invoke();
        var path = Path.Combine(outputFolder, $"report-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        // File I/O can already have started when cancellation/revocation arrives. Never report it as success.
        await stream.WriteAsync(Encoding.UTF8.GetBytes(html), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.IsCurrent(permissions)) throw new UnauthorizedAccessException("匯出期間授權已撤銷；產物可能已寫入。");
        QueryResultRetention.EnsureFresh(result, resultClock);
        modeGuard?.Invoke();
        return path;
    }
}
