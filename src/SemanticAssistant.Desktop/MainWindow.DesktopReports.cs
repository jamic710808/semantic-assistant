using System.Collections.Immutable;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

public partial class MainWindow
{
    private bool startupReportsDetected;
    private bool desktopPickerOpen;

    private string DisplayModelName => workspace.Current.Source == ModelSourceKind.Desktop
        ? localModel?.ReportDisplayName ?? "未辨識報告名稱"
        : workspace.Current.Model.Identity.Name;

    private string DisplayModelSummary() => workspace.Current.Source == ModelSourceKind.Desktop
        ? $"報告：{DisplayModelName}\n\n內部語意模型資訊：\n{MetadataInspector.Summary(workspace.Current.Model)}"
        : MetadataInspector.Summary(workspace.Current.Model);

    public async Task DetectStartupReportsAsync()
    {
        if (startupReportsDetected) return;
        startupReportsDetected = true;
        await SelectDesktopReportAsync(startup: true);
    }

    private async Task SelectDesktopReportAsync(bool startup)
    {
        if (running is not null || desktopPickerOpen) return;
        desktopPickerOpen = true;
        try
        {
            var discovery = localDiscoveryFactory();
            async Task<ImmutableArray<DesktopReportCandidate>> DiscoverAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var token = timeout.Token;
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var instances = discovery.Discover(DesktopDiscovery.DefaultRoots());
                    token.ThrowIfCancellationRequested();
                    return DesktopReportDiscovery.Describe(instances);
                }, token).WaitAsync(token);
            }
            var reports = await DiscoverAsync();
            if (!IsLoaded || running is not null) return;
            if (startup && reports.IsEmpty) return;
            var picker = new DesktopReportPicker(this, reports, DiscoverAsync);
            if (picker.ShowDialog() != true || picker.Selection is not { } selected) return;
            var instance = selected.Instance;
            // Refresh the title after selection; never match a report by title alone.
            discovery.EnsureCurrent(instance);
            var descriptor = DesktopReportDiscovery.Describe([instance]).Single();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            running = cancellation; SetBusy(true);
            ImmutableArray<LocalCatalog> catalogs;
            try { catalogs = await DesktopModelAdapter.ListCatalogsAsync(instance, discovery, cancellation.Token).WaitAsync(cancellation.Token); }
            finally { running = null; SetBusy(false); }
            if (catalogs.IsEmpty) { SourceStatus.Text = "所選報告尚未提供語意模型；請等候 Power BI 載入完成後重試。"; return; }
            var catalog = catalogs.Length == 1 ? catalogs[0]
                : SelectLocal("所選報告有多個內部模型，請選取語意模型", catalogs, item => item.Name);
            if (catalog is not null)
            {
                if (await LoadLocalAsync(new(instance, catalog.Id, discovery, descriptor.ReportName)))
                    SourceStatus.Text = $"已連接報告：{DisplayModelName}（localhost:{instance.Owner.Port}）· 已讀取模型結構";
            }
        }
        catch (OperationCanceledException) { SourceStatus.Text = "本機報告偵測／連線已取消或超時；可以重新選取報告。"; }
        catch { SourceStatus.Text = "無法偵測或連接本機報告；請確認 Power BI 仍在執行後重試。"; }
        finally { desktopPickerOpen = false; }
    }
}
