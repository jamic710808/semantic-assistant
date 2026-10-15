using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace SemanticAssistant.Desktop;

// Keep the application-owned original, and open a complete user-owned copy.
internal static class LocalHtmlReportLauncher
{
    public static string Open(string exportedPath)
    {
        var browserPath = PrepareBrowserCopy(exportedPath);
        Process.Start(new ProcessStartInfo(new Uri(browserPath).AbsoluteUri) { UseShellExecute = true });
        return browserPath;
    }

    internal static string PrepareBrowserCopy(string exportedPath)
    {
        var source = Path.GetFullPath(exportedPath);
        if (!string.Equals(Path.GetExtension(source), ".html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只能開啟已匯出的 HTML 報告。");
        var bytes = File.ReadAllBytes(source);
        if (bytes.Length is 0 or > 256_000) throw new InvalidDataException("HTML 報告大小無效。");
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) throw new IOException("找不到目前使用者的文件資料夾。");
        var folder = Path.Combine(documents, "Semantic Assistant Reports");
        Directory.CreateDirectory(folder);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var destination = Path.Combine(folder, $"semantic-report-{digest[..16]}.html");
        if (File.Exists(destination))
        {
            if (SHA256.HashData(File.ReadAllBytes(destination)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                return destination;
            // Preserve any copy the user has edited instead of replacing it.
            destination = Path.Combine(folder, $"semantic-report-{Guid.NewGuid():N}.html");
        }
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        return destination;
    }
}
