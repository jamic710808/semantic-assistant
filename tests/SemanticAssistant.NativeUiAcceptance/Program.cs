using System.IO;
using System.Text.Json;
using System.Windows;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Desktop;
using SemanticAssistant.PowerBIFixture;

namespace SemanticAssistant.NativeUiAcceptance;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? output = null;
        var stage = "arguments";
        try
        {
            if (args.Length != 4 || args[0] != "--owned-session" || args[2] != "--output-parent") return 2;
            if (new FileInfo(args[1]).Length > 16_384) return 2;
            var owned = JsonSerializer.Deserialize<OwnedDesktopVerification.OwnedSession>(File.ReadAllText(args[1])) ?? throw new InvalidDataException();
            OwnedDesktopVerification.EnsureOwned(owned);
            var parent = Path.GetFullPath(args[3]);
            if (!Directory.Exists(parent) || parent.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException();
            for (var ancestor = new DirectoryInfo(parent); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            var root = Path.Combine(parent, "native-ui-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(root)) throw new InvalidDataException();
            Directory.CreateDirectory(root);
            output = root;
            using (var file = new FileStream(Path.Combine(root, "launch.json"), FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(file, new { Version = 1, SyntheticFixtureSession = Path.GetFullPath(args[1]),
                    owned.EngineProcessId, owned.EngineStartedUtcTicks, owned.DesktopVersion, IsolatedStorage = true,
                    AutomaticUiActions = false, NativeUiVerified = false, RealAiProviderVerified = false });
            var app = new App(() => NativeWindowFactory.Create(owned, root));
            // Capture/remote desktop environments may not present WPF's hardware surface.
            // This affects only this interactive acceptance process, not product flow/policy.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            stage = "initialize_resources";
            app.InitializeComponent();
            stage = "run_window";
            return app.Run();
        }
        catch (Exception error)
        {
            if (output is not null)
            {
                using var file = new FileStream(Path.Combine(output, "startup-failure.json"), FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(file, new { Stage = stage, Type = error.GetType().Name, error.HResult,
                    Methods = new System.Diagnostics.StackTrace(error).GetFrames().Take(4).Select(f => f.GetMethod()?.Name).ToArray(),
                    NativeUiVerified = false });
            }
            return 1;
        }
    }
}

internal static class NativeWindowFactory
{
    public static MainWindow Create(OwnedDesktopVerification.OwnedSession owned, string storageRoot)
    {
        // The product App loads its own resources. Its injected factory constructs the same
        // product window with fresh local stores and one pinned engine inventory.
        OwnedDesktopVerification.EnsureOwned(owned);
        DesktopDiscovery Discovery() => new(new PinnedFixturePortInventory(new WindowsLocalPortInventory(),
            owned.EngineProcessId, owned.EngineStartedUtcTicks, () => OwnedDesktopVerification.EnsureOwned(owned)));
        var window = new MainWindow(restoreSettings: false, localDiscoveryFactory: Discovery, storageRoot: storageRoot)
        { Title = "Semantic Assistant · 合成原生驗收" };
        window.Loaded += (_, _) =>
        {
            using var file = new FileStream(Path.Combine(storageRoot, "loaded.json"), FileMode.CreateNew, FileAccess.Write);
            var content = (FrameworkElement)window.Content;
            JsonSerializer.Serialize(file, new { window.IsVisible, window.ActualWidth, window.ActualHeight,
                ContentWidth = content.ActualWidth, ContentHeight = content.ActualHeight,
                SoftwareRendering = true, NativeUiVerified = false });
        };
        return window;
    }
}
