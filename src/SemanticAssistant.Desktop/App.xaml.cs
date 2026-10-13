using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SemanticAssistant.Desktop;

public partial class App : System.Windows.Application
{
    private readonly Func<MainWindow>? startupWindowFactory;
    public App() { }
    public App(Func<MainWindow> startupWindowFactory) => this.startupWindowFactory = startupWindowFactory;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (startupWindowFactory is not null)
        {
            var isolatedWindow = startupWindowFactory();
            MainWindow = isolatedWindow;
            isolatedWindow.Show();
            return;
        }
        var smoke = (e.Args.Length == 2 && e.Args[0] is "--smoke-test" or "--smoke-offline" or "--smoke-writer" or "--smoke-dashboard" or "--smoke-objects" or "--smoke-proposal" or "--smoke-settings" or "--smoke-modes" or "--smoke-history" or "--smoke-cloudlogin") ||
            (e.Args.Length == 3 && e.Args[0] is "--smoke-mcp" or "--smoke-importedtools");
        var smokeStore = smoke ? new SemanticAssistant.Application.FileAssistantSettingsStore(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!, "preferences-fixture-" + Guid.NewGuid().ToString("N"))) : null;
        var smokeHistory = smoke ? new SemanticAssistant.Application.FileHistoryStore(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!, "history-fixture-" + Guid.NewGuid().ToString("N")), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemanticAssistant", "reports")) : null;
        var window = new MainWindow(smokeStore, restoreSettings: !smoke, historyStore: smokeHistory);
        MainWindow = window;
        window.Show();
        // Headless CI smoke still constructs and loads the actual WPF window.
        if (smoke)
        {
            window.Loaded += async (_, _) =>
            {
                try
                {
                    if (e.Args[0] == "--smoke-cloudlogin") await window.RunCloudLoginCacheSmokeAsync(e.Args[1]);
                    else if (e.Args[0] == "--smoke-importedtools") await window.RunImportedToolsSmokeAsync(e.Args[2]);
                    else if (e.Args[0] == "--smoke-history") await window.RunHistorySmokeAsync();
                    else if (e.Args[0] == "--smoke-modes") await window.RunModesSmokeAsync();
                    else if (e.Args[0] == "--smoke-settings") await window.RunSettingsSmokeAsync(e.Args[1]);
                    else if (e.Args[0] == "--smoke-objects") await window.RunObjectWriterSmokeAsync();
                    else if (e.Args[0] == "--smoke-proposal") await window.RunProposalSmokeAsync(Path.ChangeExtension(e.Args[1], ".proposal.png"));
                    else if (e.Args[0] == "--smoke-dashboard") await window.RunDashboardSmokeAsync(Path.ChangeExtension(e.Args[1], ".dashboard.png"));
                    else if (e.Args[0] == "--smoke-writer") await window.RunWriterSmokeAsync();
                    else if (e.Args[0] == "--smoke-mcp") await window.RunMcpSmokeAsync(e.Args[2]);
                    else if (e.Args[0] == "--smoke-offline") await window.RunOfflineSmokeAsync();
                    else await window.RunSmokeAsync();
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
                    var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                    {
                        drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                        drawing.DrawRectangle(new VisualBrush(content), null,
                            new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
                    }
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var image = File.Create(Path.ChangeExtension(e.Args[1], ".png"))) encoder.Save(image);
                    File.WriteAllText(e.Args[1], e.Args[0] == "--smoke-cloudlogin" ? "wpf-loaded;cloud-cache-opt-in-default-off=ok;dpapi-roundtrip=ok;clear-from-form=ok;no-restored-consent=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-importedtools" ? "wpf-loaded;mcp-explicit-import=ok;no-auto-grant=ok;per-call-refusal=ok;ai-tool-roundtrip=ok;owned-process-cleanup=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-history" ? "wpf-loaded;history-opt-in=ok;summary-artifact-index=ok;no-restored-authority=ok;clear-keeps-html=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-modes" ? "wpf-loaded;five-mode-routes=ok;mode-revocation=ok;separate-consent=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-settings" ? "wpf-loaded;settings-restart=ok;no-restored-consent=ok;saved-budget=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-objects" ? "wpf-loaded;mixed-preview=ok;column-relationship-write=ok;readback=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-proposal" ? "wpf-loaded;ai-dashboard-proposal=ok;no-auto-write-query=ok;per-operation-consent=ok;dynamic-html-write=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-dashboard" ? "wpf-loaded;dashboard-preview=ok;write-query-export-consent=ok;html-report=ok;dynamic-html-write=ok;demo-reset=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-writer" ? "wpf-loaded;write-preview=ok;per-operation-consent=ok;readback=ok;synthetic-fixture=true" :
                        e.Args[0] == "--smoke-mcp" ? "wpf-loaded;mcp-stdio=ok;per-call-consent=ok;synthetic-fixture=true" : e.Args[0] == "--smoke-offline" ?
                        "wpf-loaded;offline-tmdl=ok;search=ok;details=ok;reload=ok;demo-reset=ok;synthetic-fixture=true" :
                        "wpf-loaded;demo-query=ok;synthetic=true");
                    Shutdown(0);
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.ChangeExtension(e.Args[1], ".diagnostic.json"), System.Text.Json.JsonSerializer.Serialize(new
                    { Type = error.GetType().Name, error.HResult, Frames = error.StackTrace?.Split('\n').Take(4).ToArray(), SyntheticOnly = true }));
                    File.WriteAllText(e.Args[1], "wpf-smoke-failed");
                    Shutdown(1);
                }
            };
        }
    }
}
