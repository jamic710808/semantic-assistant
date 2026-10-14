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
        if (e.Args.Length == 2 && e.Args[0] == "--render-about")
        {
            RenderUiPreview(e.Args[1], aboutPreview: true); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--render-readable-chat")
        {
            MarkdownLayoutPreview.Write(e.Args[1]); Shutdown(0); return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--render-quick-questions" or "--render-quick-questions-cloud")
        {
            RenderUiPreview(e.Args[1], quickQuestionsPreview: e.Args[0] == "--render-quick-questions-cloud"); return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--render-permissions" or "--render-permissions-enabled")
        {
            RenderUiPreview(e.Args[1], permissionsPreview: e.Args[0] == "--render-permissions-enabled"); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--render-template-examples")
        {
            TemplatePreview.WriteExamples(Path.GetFullPath(e.Args[1])); Shutdown(0); return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--render-templates-measure" or "--render-templates-file" or "--render-templates-dashboard")
        {
            var output = e.Args[0] switch { "--render-templates-file" => SemanticAssistant.Application.ReportOutputKind.HtmlFile,
                "--render-templates-dashboard" => SemanticAssistant.Application.ReportOutputKind.Dashboard, _ => SemanticAssistant.Application.ReportOutputKind.ReportMeasure };
            RenderUiPreview(e.Args[1], templateOutput: output); return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--render-ui-preview" or "--render-report-picker" or "--render-ai-profiles")
        {
            RenderUiPreview(e.Args[1], e.Args[0] == "--render-report-picker", e.Args[0] == "--render-ai-profiles");
            return;
        }
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
        if (smoke) window.EnableSyntheticModificationFixture();
        MainWindow = window;
        if (!smoke)
            window.ContentRendered += async (_, _) => await window.DetectStartupReportsAsync();
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

    // Render our own synthetic WPF view without showing a native window,
    // restoring preferences, reading credentials or connecting to a model.
    private void RenderUiPreview(string destination, bool reportPicker = false, bool aiProfiles = false,
        SemanticAssistant.Application.ReportOutputKind? templateOutput = null, bool? permissionsPreview = null, bool? quickQuestionsPreview = null, bool aboutPreview = false)
    {
        var path = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var window = new MainWindow(restoreSettings: false, storageRoot: Path.Combine(Path.GetDirectoryName(path)!, "preview-state"));
        MainWindow = window;
        var scene = reportPicker ? (Window)new DesktopReportPicker(window,
            [new(new("synthetic-preview-workspace", new(64567, 1, 1)), "SemanticAssistantSynthetic（合成預覽）", null)],
            () => Task.FromResult(System.Collections.Immutable.ImmutableArray<SemanticAssistant.Adapters.PowerBI.Local.DesktopReportCandidate>.Empty)) : window;
        if (aiProfiles)
        {
            var sample = new SemanticAssistant.Contracts.ProviderIdentity("preview-profile-luna", "https://api.openai.com/v1/chat/completions", "gpt-6-luna");
            var previewSettings = SemanticAssistant.Application.AssistantSettings.Default with
            {
                AiProvider = sample,
                AiProfiles = [new("OpenAI Luna（合成預覽）", sample),
                    new("本機服務（合成預覽）", new("preview-profile-local", "http://127.0.0.1:1234/v1/chat/completions", "local-model"))]
            };
            scene = new AiProfilesWindow(window, previewSettings, new SemanticAssistant.Adapters.AI.WindowsCredentialVault(),
                (_, _) => throw new InvalidOperationException("合成預覽不保存設定。"));
        }
        if (templateOutput is { } output)
            scene = new TemplatesWindow(window, SemanticAssistant.Application.TemplatePreferences.Default,
                _ => throw new InvalidOperationException("合成預覽不保存設定。"), Path.Combine(Path.GetDirectoryName(path)!, "preview-state"), output);
        if (permissionsPreview is { } allowed)
            scene = new PermissionsWindow(window, allowed, _ => throw new InvalidOperationException("合成預覽不保存設定。"));
        if (quickQuestionsPreview is { } cloud)
            scene = new QuickQuestionsWindow(window, SemanticAssistant.Application.QuickQuestionSettings.Default,
                _ => throw new InvalidOperationException("合成預覽不保存設定。"), cloud);
        if (aboutPreview) scene = new AboutWindow(window);
        var content = (FrameworkElement)scene.Content;
        var size = aboutPreview ? new Size(410, 439) : reportPicker ? new Size(500, 350) : aiProfiles ? new Size(882, 618) : templateOutput is not null ? new Size(780, 738) : permissionsPreview is not null ? new Size(380, 218) : quickQuestionsPreview is not null ? new Size(490, 438) : new Size(998, 646);
        scene.Content = null;
        var frame = new System.Windows.Controls.Grid { Background = scene.Background };
        frame.Children.Add(content);
        frame.Measure(size);
        frame.Arrange(new Rect(size));
        frame.UpdateLayout();
        // Render the root at its actual coordinates. A default VisualBrush would
        // stretch its painted bounds and remove the transparent outer margins.
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        if (reportPicker || aiProfiles || templateOutput is not null || permissionsPreview is not null || quickQuestionsPreview is not null || aboutPreview) scene.Close();
        window.Close();
        Shutdown(0);
    }
}
