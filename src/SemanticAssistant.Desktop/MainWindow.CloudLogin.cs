using System.IO;
using System.Windows;
using SemanticAssistant.Adapters.PowerBI.Cloud;

namespace SemanticAssistant.Desktop;

public partial class MainWindow
{
    private void ReleaseCloud()
    {
        cloudModel?.Dispose(); cloudModel = null;
        cloudIdentity?.Dispose(); cloudIdentity = null;
    }
    private async void OnSignOutCloud(object sender, RoutedEventArgs e)
    {
        if (running is not null || cloudIdentity is null) return;
        var identity = cloudIdentity; cloudIdentity = null;
        InvalidateConversation();
        cloudModel?.Dispose(); cloudModel = null;
        workspace.ResetToDemo(); localModel = null; offlineFolder = null; demoWriter = null;
        SearchInput.Clear(); PresentSelection();
        using var cancellation = new CancellationTokenSource();
        running = cancellation; SetBusy(true);
        try { await identity.SignOutAsync(); SourceStatus.Text = "已登出雲端、清除本機登入並撤銷本次操作同意；目前為合成示範。"; }
        catch { SourceStatus.Text = "本次操作同意已撤銷，已返回示範；登入快取清除失敗，請從登入視窗再清除。"; }
        finally { identity.Dispose(); running = null; SetBusy(false); }
    }

    public async Task RunCloudLoginCacheSmokeAsync(string reportPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "SemanticAssistant-cloud-smoke-" + Guid.NewGuid().ToString("N"));
        var client = Guid.NewGuid(); var tenant = Guid.NewGuid();
        void Stage(string value) => File.WriteAllText(Path.ChangeExtension(reportPath, ".cloudlogin-stage.txt"), value);
        Stage("form-construction");
        var form = new CloudLoginWindow(this, root);
        form.Show(); await Dispatcher.InvokeAsync(() => { });
        try
        {
            Stage("default-off");
            var remember = (System.Windows.Controls.CheckBox)form.FindName("RememberCloud");
            var different = (System.Windows.Controls.CheckBox)form.FindName("DifferentCloudAccount");
            if (remember.IsChecked == true || different.IsChecked == true || Directory.Exists(root) || CloudSignOutButton.IsEnabled)
                throw new InvalidOperationException("Cloud persistence must start disabled.");
            var payload = System.Text.Encoding.UTF8.GetBytes("synthetic-cache-bytes-only");
            Stage("dpapi-save-read");
            using (var cache = new CloudLoginCache(client, tenant, root)) cache.Save("synthetic-home-account", DateTimeOffset.UtcNow.AddDays(1), payload);
            using (var cache = new CloudLoginCache(client, tenant, root))
            using (var entry = cache.Read())
                if (entry?.AccountId != "synthetic-home-account" || !entry.MsalBytes.SequenceEqual(payload)) throw new InvalidOperationException();
            ((System.Windows.Controls.TextBox)form.FindName("CloudClient")).Text = client.ToString();
            Stage("clear-from-form");
            ((System.Windows.Controls.TextBox)form.FindName("CloudTenant")).Text = tenant.ToString();
            ((System.Windows.Controls.Button)form.FindName("ClearCloudLogin")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            using (var cleared = new CloudLoginCache(client, tenant, root)) if (cleared.Read() is not null) throw new InvalidOperationException();
            if (AllowQuery.IsChecked == true || AllowExport.IsChecked == true || CloudSignOutButton.IsEnabled)
                throw new InvalidOperationException("Cache must not restore model capabilities.");
            form.UpdateLayout();
            Stage("capture-form");
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)form.ActualWidth, (int)form.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(form);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.ChangeExtension(reportPath, ".cloudlogin.png")); encoder.Save(image);
            Stage("passed");
        }
        finally { form.Close(); }
    }
}
