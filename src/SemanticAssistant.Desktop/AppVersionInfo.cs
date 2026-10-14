using System.Globalization;
using System.Reflection;

namespace SemanticAssistant.Desktop;

internal static class AppVersionInfo
{
    private static readonly Assembly DesktopAssembly = typeof(App).Assembly;
    public static string Version => DesktopAssembly.GetName().Version?.ToString(3) ?? "未知";
    public static string FullVersion => DesktopAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version;
    public static string VersionLabel => $"版本：{Version}" + (Metadata("ReleaseChannel") == "Development" ? "（開發版）" : "");
    public static string BuildTimeLabel => DateTimeOffset.TryParse(Metadata("BuildUtc"), CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time) ? $"建置：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : "建置時間：未提供";
    public static string ProgramPath => DesktopAssembly.Location;
    private static string? Metadata(string key) => DesktopAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;
}
