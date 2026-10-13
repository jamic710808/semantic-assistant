using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Mcp;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Files;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Adapters.PowerBI.Cloud;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Desktop;

public partial class MainWindow : Window
{
    private CancellationTokenSource? running;
    private PermissionSession? session;
    private QueryResult? latestResult;
    private readonly DispatcherTimer resultExpiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DashboardProposal? latestProposal;
    private Guid activeRequest;
    private readonly ModelWorkspace workspace = new(DemoModel.Snapshot);
    private readonly ToolRegistry toolRegistry = new(ToolDispatcher.Definitions);
    private Task importedCleanup = Task.CompletedTask;
    private bool allowImportedClose;
    private bool importedClosePending;
    private readonly ModeController modes = new(AssistantMode.Analyze, DemoModel.Snapshot.Identity);
    private string? offlineFolder;
    private bool uiReady;
    private ProviderIdentity? aiProfile;
    private AssistantSettings settings = AssistantSettings.Default;
    private readonly IAssistantSettingsStore settingsStore;
    private readonly IHistoryStore historyStore;
    private bool rememberAiProfile;
    private readonly WindowsCredentialVault vault = new();
    private DesktopModelAdapter? localModel;
    private CloudModelAdapter? cloudModel;
    private MsalUserIdentity? cloudIdentity;
    private DemoDefinitionWriter? demoWriter;
    private readonly Func<DesktopDiscovery> localDiscoveryFactory;
    private readonly string storageRoot;

    public MainWindow(IAssistantSettingsStore? settingsStore = null, bool restoreSettings = true, IHistoryStore? historyStore = null,
        Func<DesktopDiscovery>? localDiscoveryFactory = null, string? storageRoot = null)
    {
        this.localDiscoveryFactory = localDiscoveryFactory ?? (() => new DesktopDiscovery(new WindowsLocalPortInventory()));
        this.storageRoot = storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemanticAssistant");
        this.historyStore = historyStore ?? new FileHistoryStore(Path.Combine(this.storageRoot, "history"), Path.Combine(this.storageRoot, "reports"));
        this.settingsStore = settingsStore ?? new FileAssistantSettingsStore(Path.Combine(this.storageRoot, "preferences"));
        InitializeComponent();
        uiReady = true;
        PresentSelection();
        if (restoreSettings) RestoreSettings();
        resultExpiryTimer.Tick += (_, _) => ExpireResultIfNeeded();
        resultExpiryTimer.Start();
        Closed += (_, _) => { resultExpiryTimer.Stop(); latestResult = null; ReleaseCloud(); };
    }

    private void PresentSelection()
    {
        var current = workspace.Current;
        var model = current.Model;
        if (modes.Snapshot.Target != model.Identity) modes.Change(modes.Snapshot.Mode, model.Identity);
        ModelName.Text = model.Identity.Name;
        ModelCounts.Text = $"{model.Tables.Length} 表 · {model.Tables.Sum(t => t.Columns.Length)} 欄位\n" +
            $"{model.Tables.Sum(t => t.Measures.Length)} 度量值 · {model.Relationships.Length} 單欄關係";
        SourceStatus.Text = current.Source == ModelSourceKind.Cloud ? "Power BI 雲端 · XMLA 唯讀結構 · REST 受限查詢" :
            current.Source == ModelSourceKind.Desktop ? "Power BI Desktop · 已選取模型 · 連線只讀取結構" :
            current.IsOffline ? "離線 TMDL · 本機結構檢視 · 未執行查詢、未送 AI" : "合成模型 · 本機示範 · 模擬 AI";
        PolicyHint.Text = current.IsOffline ? "離線來源不提供數值查詢。AI 結構分析需設定供應商並逐回合同意外送。" :
            current.Source is ModelSourceKind.Desktop or ModelSourceKind.Cloud ? "數值查詢預設關閉；AI 外送另需逐回合同意。取消撤銷當次授權。" : "目前為合成示範；取消會撤銷當次回合。";
        DetailTitle.Text = "模型概覽";
        MetadataDetails.Text = MetadataInspector.Summary(model);
        ResultTitle.Text = current.IsOffline ? "離線檔案來源" : "查詢結果與來源";
        ResultDetails.Text = current.IsOffline ? $"來源類型：TMDL 定義目錄\n\n{offlineFolder}\n\n內容版本：\n{model.Identity.Revision}\n\n只讀取檔案中的結構。\n未啟動 Power BI 連線。\n未讀取事實資料列。\n\n選取左側物件查看型別、DAX 或關係。" : "尚未執行數值查詢。";
        MessageInput.Text = current.Source != ModelSourceKind.Synthetic && aiProfile is null ? "請從左側選取模型物件；AI 分析需先設定供應商" : "介紹模型結構";
        ChatTab.IsEnabled = current.Source == ModelSourceKind.Synthetic || aiProfile is not null;
        ContentTabs.SelectedIndex = current.Source != ModelSourceKind.Synthetic ? 1 : 0;
        BuildModelTree();
        SetBusy(running is not null);
    }

    private void BuildModelTree()
    {
        ModelTree.Items.Clear();
        var nodes = MetadataInspector.Build(workspace.Current.Model, SearchInput.Text);
        foreach (var node in nodes) ModelTree.Items.Add(ToTreeItem(node, !string.IsNullOrWhiteSpace(SearchInput.Text)));
        if (nodes.IsEmpty)
        {
            DetailTitle.Text = "沒有符合的物件";
            MetadataDetails.Text = "請調整搜尋字詞。搜尋只比對物件名稱，不修改模型。";
        }
    }

    private static TreeViewItem ToTreeItem(MetadataNode node, bool expand)
    {
        var item = new TreeViewItem { Header = node.Title, Tag = node, IsExpanded = expand || node.Id is "tables" or "relationships" };
        foreach (var child in node.Children) item.Items.Add(ToTreeItem(child, expand));
        return item;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (uiReady) BuildModelTree();
    }

    private void OnMetadataSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: MetadataNode node }) return;
        DetailTitle.Text = node.Title;
        MetadataDetails.Text = node.Detail;
        ContentTabs.SelectedIndex = 1;
    }

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "選取 PBIP 語意模型的 definition（TMDL）目錄", Multiselect = false };
        if (picker.ShowDialog(this) == true) await LoadFolderAsync(picker.FolderName);
    }

    private async void OnOpenExample(object sender, RoutedEventArgs e) =>
        await LoadFolderAsync(Path.Combine(AppContext.BaseDirectory, "Examples", "definition"));

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (cloudModel is not null) await LoadCloudAsync(cloudModel);
        else if (localModel is not null) await LoadLocalAsync(localModel);
        else if (offlineFolder is not null) await LoadFolderAsync(offlineFolder);
    }

    private async void OnOpenCloud(object sender, RoutedEventArgs e)
    {
        if (running is not null || AiDialogs.CloudConfiguration(this) is not { } configuration) return;
        CloudModelAdapter? candidate = null;
        MsalUserIdentity? candidateIdentity = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        running = cancellation; SetBusy(true);
        try
        {
            var identity = candidateIdentity = new MsalUserIdentity(configuration.ClientId, configuration.TenantId, configuration.RememberLogin);
            await identity.SignInAsync(cancellation.Token, configuration.SelectDifferentAccount);
            using var rest = new PowerBiRestClient(identity, configuration.TenantId);
            CloudWorkspace? workspaceChoice = null; int offset = 0;
            while (true)
            {
                var page = await rest.ListWorkspacesAsync(offset, cancellation.Token);
                var choices = page.NextOffset is null ? page.Items : page.Items.Add(new(Guid.Empty, "載入下一頁工作區…"));
                workspaceChoice = SelectLocal("選取已登入租用戶的工作區", choices, w => w.Name);
                if (workspaceChoice is null) return;
                if (workspaceChoice.Id != Guid.Empty) break;
                offset = page.NextOffset!.Value;
            }
            var datasets = await rest.ListDatasetsAsync(workspaceChoice.Id, cancellation.Token);
            var selected = SelectLocal("選取資料集；只讀結構，不查詢資料列", datasets, d => d.Name);
            if (selected is null) return;
            candidate = new(identity, configuration.TenantId, workspaceChoice, selected);
        }
        catch (CloudLoginCacheException) { SourceStatus.Text = "登入快取不可用；若正在使用相同設定，請先登出雲端。亦可在登入視窗清除本機快取後重試。"; }
        catch { SourceStatus.Text = "雲端登入／清單失敗或已取消；請檢查 app／租用戶、帳號與權限，保留原來源。"; }
        finally { running = null; if (candidate is null) candidateIdentity?.Dispose(); SetBusy(false); }
        if (candidate is not null)
        {
            if (await LoadCloudAsync(candidate)) { cloudIdentity = candidateIdentity; SetBusy(false); }
            else { candidate.Dispose(); candidateIdentity?.Dispose(); }
        }
    }

    private async Task<bool> LoadCloudAsync(CloudModelAdapter candidate)
    {
        if (running is not null) return false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        running = cancellation; SetBusy(true);
        try
        {
            if (!await workspace.LoadAsync(candidate, ModelSourceKind.Cloud, cancellation.Token)) return false;
            InvalidateConversation();
            if (cloudModel != candidate) ReleaseCloud();
            cloudModel = candidate; localModel = null; offlineFolder = null;
            SearchInput.Clear(); PresentSelection(); return true;
        }
        catch { SourceStatus.Text = "XMLA 結構讀取失敗／超時，保留原來源。請檢查容量、XMLA read、Build 權限與資料集 ID。"; return false; }
        finally { running = null; SetBusy(false); }
    }

    private async void OnOpenLocal(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        try
        {
            var discovery = localDiscoveryFactory();
            var instances = discovery.Discover(DesktopDiscovery.DefaultRoots());
            if (instances.IsEmpty) { SourceStatus.Text = "沒有可用的 Power BI Desktop instance；請開啟測試模型後重新列舉。"; return; }
            var instance = SelectLocal("選取本機 Desktop instance（接著只讀結構）", instances,
                item => $"埠 {item.Owner.Port} · PID {item.Owner.ProcessId} · {Path.GetFileName(item.Workspace)}");
            if (instance is null) return;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            running = cancellation; SetBusy(true);
            ImmutableArray<LocalCatalog> catalogs;
            try { catalogs = await DesktopModelAdapter.ListCatalogsAsync(instance, discovery, cancellation.Token).WaitAsync(cancellation.Token); }
            finally { running = null; SetBusy(false); }
            var catalog = SelectLocal("選取語意模型；連線不讀事實資料列", catalogs, item => item.Name);
            if (catalog is not null) await LoadLocalAsync(new(instance, catalog.Id, discovery));
        }
        catch { SourceStatus.Text = "無法列舉／連線本機模型。請確認 Desktop 仍開啟，重新選取 instance；保留原來源。"; }
    }

    private T? SelectLocal<T>(string title, IEnumerable<T> values, Func<T, string> display) where T : class
    {
        var items = values.ToArray();
        var list = new ListBox { ItemsSource = items.Select(display).ToArray(), SelectedIndex = 0, Margin = new Thickness(16) };
        var button = new Button { Content = "選取", Margin = new Thickness(16) };
        var panel = new DockPanel(); DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button); panel.Children.Add(list);
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 660,
            Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        button.Click += (_, _) => { if (list.SelectedIndex >= 0) dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? items[list.SelectedIndex] : null;
    }

    private async Task<bool> LoadLocalAsync(DesktopModelAdapter adapter)
    {
        if (running is not null) return false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        running = cancellation; SetBusy(true);
        try
        {
            if (!await workspace.LoadAsync(adapter, ModelSourceKind.Desktop, cancellation.Token)) return false;
            InvalidateConversation(); ReleaseCloud(); localModel = adapter; offlineFolder = null;
            SearchInput.Clear(); PresentSelection(); return true;
        }
        catch { SourceStatus.Text = "Desktop 讀取失敗／超時，保留原模型。關閉或埠變動後請重新選取 instance。"; return false; }
        finally { running = null; SetBusy(false); }
    }

    private async Task<bool> LoadFolderAsync(string selectedFolder)
    {
        if (running is not null) return false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        running = cancellation;
        SetBusy(true);
        SourceStatus.Text = "正在本機讀取 TMDL 結構；可按「取消」。";
        try
        {
            var loaded = await workspace.LoadOfflineAsync(new TmdlModelReader(selectedFolder), cancellation.Token);
            if (!loaded) return false;
            InvalidateConversation();
            localModel = null;
            ReleaseCloud();
            offlineFolder = Path.GetFullPath(selectedFolder);
            SearchInput.Clear();
            PresentSelection();
            return true;
        }
        catch (OperationCanceledException)
        {
            SourceStatus.Text = cancellation.IsCancellationRequested ? "讀取已取消或超時，保留原模型。" : "讀取未完成，保留原模型。";
            return false;
        }
        catch
        {
            SourceStatus.Text = "讀取失敗，保留原模型。請確認選取 definition 目錄、TMDL 格式與檔案大小。";
            return false;
        }
        finally
        {
            running = null;
            SetBusy(false);
        }
    }

    private void OnReturnToDemo(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        InvalidateConversation();
        workspace.ResetToDemo();
        demoWriter = null;
        localModel = null;
        ReleaseCloud();
        offlineFolder = null;
        SearchInput.Clear();
        PresentSelection();
    }

    private void InvalidateConversation()
    {
        ClearImportedTools();
        modes.Change(modes.Snapshot.Mode, workspace.Current.Model.Identity);
        if (session is not null)
            session.Update(session.Snapshot.Target, session.Snapshot.Mode, ImmutableHashSet<Capability>.Empty);
        session = null;
        activeRequest = Guid.Empty;
        latestResult = null;
        latestProposal = null;
        ProposalButton.IsEnabled = false;
        AllowQuery.IsChecked = false;
        AllowExport.IsChecked = false;
        ExportButton.IsEnabled = false;
        Transcript.Text = "模型來源或 AI 設定已切換；先前回合、查詢結果與授權已清除。";
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = modes.Snapshot;
            switch (ModeController.Get(snapshot.Mode).Route)
            {
                case ModeRoute.Conversation: await RunTurnAsync(); break;
                case ModeRoute.HtmlFile: await ExportReportAsync(modes, snapshot, true); break;
                case ModeRoute.HtmlMeasure: await OpenHtmlMeasureAsync(modes, snapshot); break;
                case ModeRoute.Dashboard: await OpenDashboardAsync(modeGuard: () => modes.EnsureRoute(snapshot, ModeRoute.Dashboard)); break;
            }
        }
        catch { SourceStatus.Text = "目前模式操作未完成；請確認來源、結果與當次同意。"; }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!uiReady) return;
        if (ModePicker.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<AssistantMode>(tag, out var mode) || !Enum.IsDefined(mode))
        { SendButton.IsEnabled = false; return; }
        if (mode == modes.Snapshot.Mode) return;
        ClearImportedTools();
        modes.Change(mode, workspace.Current.Model.Identity);
        if (session is not null) session.Update(session.Snapshot.Target, session.Snapshot.Mode, []);
        session = null; activeRequest = Guid.Empty; running?.Cancel(); latestProposal = null;
        AllowQuery.IsChecked = false; AllowExport.IsChecked = false;
        SourceStatus.Text = "模式已切換；舊回合與同意已撤銷。新操作需重新同意。";
        SetBusy(running is not null);
    }

    private void OnOpenMcp(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        new McpToolsWindow(this, workspace.Current.Model.Identity,
            ModeController.Get(modes.Snapshot.Mode).Route == ModeRoute.Conversation ? ImportExternalTool : null).ShowDialog();
        SetBusy(false);
    }
    private void ImportExternalTool(McpAiToolBinding binding)
    {
        if (binding.Descriptor.Target != workspace.Current.Model.Identity || running is not null) throw new ToolPolicyException("target_changed");
        toolRegistry.ImportAfterUserSelection(binding);
        session?.Update(session.Snapshot.Target, session.Snapshot.Mode, []);
        session = null; AllowExternalTools.IsChecked = false; AllowQuery.IsChecked = false; AllowExport.IsChecked = false;
        SourceStatus.Text = "外部工具已明確匯入；請另勾選當次回合使用，操作仍需逐次同意。";
        SetBusy(false);
    }
    private void OnClearImportedTools(object sender, RoutedEventArgs e)
    {
        ClearImportedTools(); session?.Update(session.Snapshot.Target, session.Snapshot.Mode, []);
        activeRequest = Guid.Empty; running?.Cancel(); SetBusy(running is not null);
        SourceStatus.Text = "已撤銷匯入與當次工具授權；新的操作仍需明確同意。";
    }
    private void ClearImportedTools()
    {
        var bindings = toolRegistry.ClearExternal(); AllowExternalTools.IsChecked = false;
        if (!bindings.IsEmpty) importedCleanup = DisposeImportedAsync(importedCleanup, bindings);
    }
    private static async Task DisposeImportedAsync(Task previous, ImmutableArray<IExternalToolBinding> bindings)
    {
        await previous;
        foreach (var binding in bindings) { try { await binding.DisposeAsync(); } catch { /* Own process cleanup error; no environment details. */ } }
    }

    private async void OnOpenWriter(object sender, RoutedEventArgs e)
    {
        if (running is not null || workspace.Current.IsOffline || workspace.Current.Model.Tables.IsEmpty) return;
        IModelDefinitionWriter writer;
        if (cloudModel is not null) writer = cloudModel.CreateWriter();
        else if (localModel is not null) writer = localModel.CreateWriter();
        else writer = demoWriter ??= new DemoDefinitionWriter();
        var window = new ModelWriteWindow(this, writer, workspace.Current.Model.Identity, workspace.Current.Model.Tables[0].Name, storageRoot: storageRoot);
        window.ShowDialog();
        if (!window.Attempted) return;
        InvalidateConversation();
        if (cloudModel is not null) await LoadCloudAsync(cloudModel);
        else if (localModel is not null) await LoadLocalAsync(localModel);
        else if (demoWriter is not null)
        { await workspace.LoadAsync(demoWriter, ModelSourceKind.Synthetic, default); PresentSelection(); }
    }

    private async void OnOpenDashboard(object sender, RoutedEventArgs e) => await OpenDashboardAsync();
    private async void OnOpenDashboardProposal(object sender, RoutedEventArgs e)
    {
        if (latestProposal is null) return;
        try { DashboardProposals.Validate(latestProposal, workspace.Current.Model); await OpenDashboardAsync(latestProposal); }
        catch { latestProposal = null; ProposalButton.IsEnabled = false; ResultDetails.Text = "Dashboard提案已失效；請重新讀取來源並產生新提案。"; }
    }
    private async Task OpenDashboardAsync(DashboardProposal? proposal = null, Action? modeGuard = null)
    {
        if (running is not null || workspace.Current.IsOffline || !workspace.Current.Model.Tables.Any(t => !t.Columns.IsEmpty)) return;
        if (modeGuard is null)
        {
            var operation = new ModeController(AssistantMode.Dashboard, workspace.Current.Model.Identity);
            var state = operation.Snapshot; modeGuard = () => operation.EnsureRoute(state, ModeRoute.Dashboard);
        }
        IModelDefinitionWriter writer = cloudModel is not null ? cloudModel.CreateWriter() : localModel is not null ? localModel.CreateWriter() : demoWriter ??= new DemoDefinitionWriter();
        ISemanticModelReader reader = cloudModel is not null ? cloudModel : localModel is not null ? localModel : demoWriter!;
        IReadOnlyDaxQueryRunner runner = cloudModel is not null ? cloudModel : localModel is not null ? localModel : demoWriter!;
        var dialog = new DashboardWindow(this, reader, writer, runner, workspace.Current.Model, proposal,
            settings.Budgets.QueryLimits(cloudModel is not null), modeGuard,
            artifactRecorded: path => TryHistory(() => historyStore.RecordArtifact(AssistantMode.Dashboard, workspace.Current.Source, path)), storageRoot: storageRoot); dialog.ShowDialog();
        if (!dialog.Attempted) return;
        InvalidateConversation();
        if (cloudModel is not null) await LoadCloudAsync(cloudModel);
        else if (localModel is not null) await LoadLocalAsync(localModel);
        else { await workspace.LoadAsync(demoWriter!, ModelSourceKind.Synthetic, default); PresentSelection(); }
    }

    public async Task RunDashboardSmokeAsync(string? previewPath = null)
    {
        var writer = new DemoDefinitionWriter();
        var dialog = new DashboardWindow(this, writer, writer, writer, DemoModel.Snapshot); dialog.Show();
        try { await dialog.RunSyntheticSmokeAsync(previewPath); }
        finally { dialog.Close(); }
        demoWriter = writer; await workspace.LoadAsync(writer, ModelSourceKind.Synthetic, default); PresentSelection();
        // Resetting the demo must discard its old mutable adapter and revision.
        OnReturnToDemo(this, new());
        if (demoWriter is not null || workspace.Current.Model.Identity != DemoModel.Snapshot.Identity) throw new InvalidOperationException("Demo reset failed.");
        await RunSmokeAsync();
    }

    public async Task RunProposalSmokeAsync(string? previewPath = null)
    {
        AllowQuery.IsChecked = false; ModePicker.SelectedIndex = 1; MessageInput.Text = "建立合成Dashboard提案";
        var outcome = await RunTurnAsync();
        if (outcome.Code != "ok" || latestProposal is null || latestResult is not null || !ProposalButton.IsEnabled || session?.Snapshot.Allows(Capability.DefinitionWrite) == true)
            throw new InvalidOperationException("AI proposal must not grant writes or query rows.");
        OnCancel(this, new()); if (latestProposal is not null || ProposalButton.IsEnabled) throw new InvalidOperationException("Proposal cancellation failed.");
        outcome = await RunTurnAsync(); if (outcome.Code != "ok" || latestProposal is null) throw new InvalidOperationException("New proposal failed.");
        var writer = new DemoDefinitionWriter();
        if ((await writer.ReadDefinitionAsync(default)).Revision != "demo-definition-0") throw new InvalidOperationException("Proposal changed model.");
        var dialog = new DashboardWindow(this, writer, writer, writer, workspace.Current.Model, latestProposal); dialog.Show();
        try { await dialog.RunSyntheticSmokeAsync(previewPath); }
        finally { dialog.Close(); }
        InvalidateConversation(); demoWriter = writer;
        await workspace.LoadAsync(writer, ModelSourceKind.Synthetic, default); PresentSelection();
        if (latestProposal is not null || ProposalButton.IsEnabled) throw new InvalidOperationException("Source change retained proposal.");
        OnReturnToDemo(this, new());
    }

    public async Task RunWriterSmokeAsync()
    {
        var writer = new DemoDefinitionWriter();
        var window = new ModelWriteWindow(this, writer, DemoModel.Snapshot.Identity, "銷售"); window.Show();
        try { await window.RunSyntheticSmokeAsync(); }
        finally { window.Close(); }
        await workspace.LoadAsync(writer, ModelSourceKind.Synthetic, default); demoWriter = writer; PresentSelection();
        if (!workspace.Current.Model.Tables[0].Measures.Any(m => m.Name == "SA_新指標")) throw new InvalidOperationException("Writer source reload failed.");
    }

    public async Task RunObjectWriterSmokeAsync()
    {
        var writer = new DemoDefinitionWriter();
        var window = new ModelWriteWindow(this, writer, DemoModel.Snapshot.Identity, "銷售", initialObjects: DemoModelChanges.Mixed); window.Show();
        try { await window.RunSyntheticSmokeAsync(); }
        finally { window.Close(); }
        await workspace.LoadAsync(writer, ModelSourceKind.Synthetic, default); demoWriter = writer; PresentSelection();
        var snapshot = workspace.Current.Model;
        if (!snapshot.Tables[0].Columns.Any(c => c.Name == "SA_月份鍵") || !snapshot.Relationships.Any(r => r.Name == "SA_月份關係" && !r.IsActive))
            throw new InvalidOperationException("Mixed object source reload failed.");
    }

    public async Task RunMcpSmokeAsync(string fixtureDll)
    {
        var window = new McpToolsWindow(this, workspace.Current.Model.Identity);
        window.Show();
        try { await window.RunSyntheticSmokeAsync(fixtureDll); }
        finally { await window.ShutdownAsync(); }
    }

    private void OnAiConfig(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        var selected = AiDialogs.Configure(this, aiProfile, vault, rememberAiProfile);
        if (selected is not { } choice) return;
        InvalidateConversation();
        aiProfile = choice.Identity; rememberAiProfile = choice.Remember;
        settings = settings with { AiProvider = rememberAiProfile ? aiProfile : null };
        AiProviderLabel.Text = $"AI：{aiProfile.Model}\n{aiProfile.Endpoint}";
        PresentSelection();
        PersistSettings();
        if (workspace.Current.IsOffline) MessageInput.Text = "介紹模型結構";
    }

    private void OnAiDemo(object sender, RoutedEventArgs e)
    {
        InvalidateConversation();
        aiProfile = null;
        rememberAiProfile = false; settings = settings with { AiProvider = null };
        AiProviderLabel.Text = "AI：本機模擬";
        PresentSelection();
        PersistSettings();
    }

    private void RestoreSettings()
    {
        var loaded = settingsStore.Load(); settings = loaded.Settings;
        aiProfile = settings.AiProvider; rememberAiProfile = aiProfile is not null;
        AiProviderLabel.Text = aiProfile is null ? "AI：本機模擬" : $"AI：{aiProfile.Model}\n{aiProfile.Endpoint}";
        PresentSelection();
        if (loaded.Status is SettingsLoadStatus.Invalid or SettingsLoadStatus.Unsupported or SettingsLoadStatus.Unavailable)
            SourceStatus.Text = "本機設定無法載入；使用預設示範並保留原設定檔。";
    }

    private void PersistSettings()
    {
        try { settingsStore.Save(settings); SourceStatus.Text = "本機設定已保存；操作與外送仍需重新同意。"; }
        catch { SourceStatus.Text = "設定已套用本次視窗，保存未成功；原設定檔保留，重新啟動可能回到原設定。"; }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (running is not null || AiDialogs.ConfigureBudgets(this, settings.Budgets) is not { } budgets) return;
        InvalidateConversation(); settings = settings with { Budgets = budgets };
        PersistSettings(); SetBusy(false);
    }

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        new HistoryWindow(this, historyStore).ShowDialog();
    }

    private void TryHistory(Action action)
    {
        try { action(); }
        catch { SourceStatus.Text = "操作完成，歷史保存未成功；原紀錄保留。"; }
    }

    public async Task RunImportedToolsSmokeAsync(string fixtureDll)
    {
        UpdateLayout();
        if (ModelTree.ActualHeight < 75) throw new InvalidOperationException("Model tree hidden by policy controls.");
        if (toolRegistry.Snapshot.Tools.Any(t => t.Binding is not null) || AllowExternalTools.IsChecked == true) throw new InvalidOperationException("Auto import enabled.");
        var importWindow = new McpToolsWindow(this, workspace.Current.Model.Identity, ImportExternalTool); importWindow.Show();
        await importWindow.RunSyntheticImportSmokeAsync(fixtureDll);
        var entry = toolRegistry.Snapshot.Tools.Single(t => t.Origin == ToolOrigin.McpProcess);
        using var ownedProcess = Process.GetProcessById(((McpAiToolBinding)entry.Binding!).ProcessId);
        _ = ownedProcess.Handle; // Pin the exact self-created fixture process, never enumerate user processes.
        var provider = new ImportedToolSmokeProvider();
        var noGrant = await RunTurnAsync(provider, (_, _) => throw new InvalidOperationException("No grant reached confirmation."));
        if (noGrant.Code != "ok" || !noGrant.ToolResults.IsEmpty) throw new InvalidOperationException("Import granted execution.");
        AllowExternalTools.IsChecked = true;
        var denied = await RunTurnAsync(provider, (_, _) => Task.FromResult(false));
        if (denied.Code != "ok" || denied.ToolResults.Single().Code != "external_denied" || AllowExternalTools.IsChecked == true)
            throw new InvalidOperationException("Per-call refusal or turn consent reset failed.");
        AllowExternalTools.IsChecked = true;
        var confirmations = 0;
        var success = await RunTurnAsync(provider, (preview, _) =>
        {
            if (preview.OriginalName != "synthetic.echo" || preview.Source != entry.Source || preview.Arguments.GetProperty("message").GetString() != "AI合成工具測試")
                throw new InvalidOperationException("Wrong preview.");
            confirmations++; return Task.FromResult(true);
        });
        var result = success.ToolResults.Single();
        if (success.Code != "ok" || confirmations != 1 || result.PayloadCategory != EgressCategory.ExternalToolData ||
            result.Payload!.Value.GetProperty("StructuredContent").GetProperty("Calls").GetInt32() != 1)
            throw new InvalidOperationException("Imported tool result/count failed.");
        OnClearImportedTools(this, new()); await importedCleanup;
        await ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        if (entry.Binding!.IsCurrent || toolRegistry.Snapshot.Tools.Any(t => t.Binding is not null) || AllowExternalTools.IsChecked == true)
            throw new InvalidOperationException("Imported process cleanup failed.");
    }
    private sealed class ImportedToolSmokeProvider : IAiProvider
    {
        public bool UsesExternalNetwork => false;
        public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield(); token.ThrowIfCancellationRequested();
            var tool = request.Tools.FirstOrDefault(t => t.RequiredCapability == Capability.ExternalTool);
            if (tool is not null && request.Messages.IsEmpty)
                yield return new ProviderEvent.Call(new("synthetic-import", tool.Name, ContractJson.Element(new { message = "AI合成工具測試" })));
            else yield return new ProviderEvent.Text("自製匯入流程驗證完成。");
        }
    }

    public async Task RunHistorySmokeAsync()
    {
        MessageInput.Text = "介紹模型結構";
        if ((await RunTurnAsync()).Code != "ok" || historyStore.Load().Status != HistoryLoadStatus.Missing)
            throw new InvalidOperationException("History saved without opt-in.");
        var dialog = new HistoryWindow(this, historyStore); dialog.Show();
        try { dialog.EnableSyntheticSmoke(); } finally { dialog.Close(); }
        AllowQuery.IsChecked = true; MessageInput.Text = "分析合成數值";
        if ((await RunTurnAsync()).Code != "ok") throw new InvalidOperationException("History demo turn failed.");
        AllowExport.IsChecked = true;
        var scope = new ModeController(AssistantMode.HtmlFile, workspace.Current.Model.Identity);
        var path = await ExportReportAsync(scope, scope.Snapshot, false);
        if (path is null || historyStore.Load().Snapshot.Entries.Length != 2) throw new InvalidOperationException("History export index failed.");
        var restored = new MainWindow(settingsStore, restoreSettings: false, historyStore: historyStore); restored.Show();
        try
        {
            if (restored.latestResult is not null || restored.session is not null || restored.AllowQuery.IsChecked == true || restored.AllowExport.IsChecked == true)
                throw new InvalidOperationException("History restored results or authority.");
            var loaded = new HistoryWindow(restored, historyStore); loaded.Show();
            try { loaded.ListClearSyntheticSmoke(); } finally { loaded.Close(); }
        }
        finally { restored.Close(); }
        if (!File.Exists(path) || !historyStore.Load().Snapshot.Entries.IsEmpty) throw new InvalidOperationException("History clear deleted artifact or kept entries.");
    }

    public async Task RunSettingsSmokeAsync(string reportPath)
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "settings-fixture-" + Guid.NewGuid().ToString("N"));
        var store = new FileAssistantSettingsStore(folder);
        var profile = new ProviderIdentity("settings-fixture", "https://example.invalid/v1/chat/completions", "synthetic");
        var saved = AssistantSettings.Default with { AiProvider = profile, Budgets = new(QueryRows: 2) };
        store.Save(saved);
        var restored = new MainWindow(store, historyStore: historyStore);
        restored.Show();
        try
        {
            if (restored.aiProfile != profile || restored.settings.Budgets.QueryRows != 2 || restored.session is not null ||
                restored.AllowQuery.IsChecked == true || restored.AllowExport.IsChecked == true || restored.latestResult is not null ||
                restored.workspace.Current.Model.Identity != DemoModel.Snapshot.Identity)
                throw new InvalidOperationException("Settings restored source or permissions.");
            // No external service is used: opt out of profile retention, then run the real demo path.
            restored.OnAiDemo(restored, new());
            if (store.Load().Settings.AiProvider is not null) throw new InvalidOperationException("Demo switch kept remembered profile.");
            restored.AllowQuery.IsChecked = true; restored.MessageInput.Text = "分析合成數值";
            var result = await restored.RunTurnAsync();
            if (result.Code != "ok" || restored.latestResult?.Rows.Length != 2 || restored.latestResult.Truncated != true)
                throw new InvalidOperationException("Saved row budget was not applied.");
        }
        finally { restored.Close(); }
        if (File.ReadAllText(Path.Combine(folder, "settings.json")).Contains("Granted"))
            throw new InvalidOperationException("Settings persisted authorization.");
    }

    public async Task RunModesSmokeAsync()
    {
        ModePicker.SelectedIndex = 1; AllowQuery.IsChecked = true; MessageInput.Text = "分析合成數值";
        if ((await RunTurnAsync()).Code != "ok" || latestResult is null || !ResultDetails.Text.Contains("報告規格已驗證"))
            throw new InvalidOperationException("Report route failed.");
        var oldSession = session!; var oldPermissions = oldSession.Snapshot; var oldMode = modes.Snapshot;
        ModePicker.SelectedIndex = 2;
        if (AllowQuery.IsChecked == true || AllowExport.IsChecked == true || session is not null || oldSession.IsCurrent(oldPermissions) || latestResult is null)
            throw new InvalidOperationException("Mode switch kept consent or lost a still-fresh result.");
        try { modes.EnsureRoute(oldMode, ModeRoute.Conversation); throw new InvalidOperationException("Old mode accepted."); }
        catch (ModePolicyException) { }
        AllowExport.IsChecked = true;
        var path = await ExportReportAsync(modes, modes.Snapshot, false);
        if (path is null || !File.ReadAllText(path).Contains("397,000.00") || AllowExport.IsChecked == true || session!.Snapshot.Granted.Count != 0)
            throw new InvalidOperationException("HTML file route or independent consent failed.");
        ModePicker.SelectedIndex = 3;
        var state = modes.Snapshot;
        var writer = new DemoDefinitionWriter(); var source = latestResult!;
        var change = DashboardPlans.HtmlMeasure("銷售", "SA_ModeHTML", source, ReportSpecs.Default(source));
        var stale = new ModelWriteWindow(this, writer, source.Source, change.Table, [change], expectedSource: source.Source,
            sourceResult: source, modeGuard: () => modes.EnsureRoute(state, ModeRoute.HtmlMeasure));
        stale.Show(); modes.Change(AssistantMode.Analyze, source.Source);
        try { await stale.RunStalePreviewSmokeAsync(); } finally { stale.Close(); }
        modes.Change(AssistantMode.HtmlMeasure, source.Source); state = modes.Snapshot;
        var html = new ModelWriteWindow(this, writer, source.Source, change.Table, [change], expectedSource: source.Source,
            sourceResult: source, modeGuard: () => modes.EnsureRoute(state, ModeRoute.HtmlMeasure));
        html.Show(); try { await html.RunSyntheticSmokeAsync(); } finally { html.Close(); }
        if (html.LastOutcome?.Success != true) throw new InvalidOperationException("HTML measure route failed.");
        ModePicker.SelectedIndex = 4; var dashboardState = modes.Snapshot;
        var dashboardWriter = new DemoDefinitionWriter();
        var dashboard = new DashboardWindow(this, dashboardWriter, dashboardWriter, dashboardWriter, DemoModel.Snapshot,
            modeGuard: () => modes.EnsureRoute(dashboardState, ModeRoute.Dashboard));
        dashboard.Show(); try { await dashboard.RunSyntheticSmokeAsync(); } finally { dashboard.Close(); }
        ModePicker.SelectedIndex = 0;
        if (ModeController.Get(modes.Snapshot.Mode).Route != ModeRoute.Conversation || AllowQuery.IsChecked == true || AllowExport.IsChecked == true)
            throw new InvalidOperationException("Analyze route restored approvals.");
    }

    private async Task<ConversationOutcome> RunTurnAsync(IAiProvider? providerOverride = null,
        Func<ExternalToolPreview, CancellationToken, Task<bool>>? confirmationOverride = null)
    {
        await importedCleanup;
        var sourceKind = workspace.Current.Source;
        var modeSnapshot = modes.Snapshot;
        modes.EnsureRoute(modeSnapshot, ModeRoute.Conversation);
        if (running is not null) throw new InvalidOperationException("回合正在執行。");
        if (workspace.Current.Source != ModelSourceKind.Synthetic && aiProfile is null) return new(Guid.NewGuid(), "inspection_only", []);
        var consent = aiProfile is null ? null : AiDialogs.Consent(this, aiProfile, workspace.Current.Model.Identity,
            !workspace.Current.IsOffline && AllowQuery.IsChecked == true, AllowExternalTools.IsChecked == true);
        if (aiProfile is not null && consent is null) return new(Guid.NewGuid(), "egress_denied", []);
        latestResult = null;
        latestProposal = null;
        ProposalButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ResultDetails.Text = "尚未執行數值查詢。";
        var grants = ImmutableHashSet.Create(Capability.MetadataRead);
        if (!workspace.Current.IsOffline && AllowQuery.IsChecked == true) grants = grants.Add(Capability.DataQuery);
        if (AllowExternalTools.IsChecked == true && toolRegistry.Snapshot.Tools.Any(t => t.Binding?.IsCurrent == true)) grants = grants.Add(Capability.ExternalTool);
        var mode = ModeController.Get(modeSnapshot.Mode).PermissionMode;
        session = new(workspace.Current.Model.Identity, mode, grants, consent);
        var turnSession = session;
        var model = new DemoModel();
        ISemanticModelReader reader = cloudModel is not null ? cloudModel : localModel is not null ? localModel : workspace.Current.IsOffline ? new TmdlModelReader(offlineFolder!) : demoWriter is not null ? demoWriter : model;
        IReadOnlyDaxQueryRunner queryRunner = cloudModel is not null ? cloudModel : localModel is not null ? localModel : demoWriter is not null ? demoWriter : model;
        using var external = aiProfile is null ? null : new OpenAiCompatibleProvider(aiProfile, vault);
        IAiProvider provider = providerOverride ?? (external is null ? new DemoAiProvider() : external);
        var queryLimits = settings.Budgets.QueryLimits(cloudModel is not null);
        var coordinator = new ConversationCoordinator(reader, provider, new ToolDispatcher(reader, queryRunner, session, queryLimits,
            modes: modes, modeSnapshot: modeSnapshot, toolRegistry: toolRegistry,
            externalConfirmation: confirmationOverride ?? ((preview, token) => AiDialogs.ConfirmExternalAsync(this, preview, token))), session,
            settings.Budgets.TurnLimits(), new ModelContextBuilder(budget: new(settings.Budgets.ContextTables, settings.Budgets.ContextCharacters)),
            modes: modes, modeSnapshot: modeSnapshot);
        running = new CancellationTokenSource();
        activeRequest = Guid.NewGuid();
        var request = new ConversationRequest(activeRequest, MessageInput.Text, mode, session.Snapshot);
        if (consent is not null) SourceStatus.Text = $"已授權當次 AI 回合 · {consent.Provider.Endpoint} · {string.Join("、", consent.Categories)}";
        Transcript.Text += $"\n\n你：{request.Message}\n";
        ContentTabs.SelectedIndex = 0;
        SetBusy(true);
        try
        {
            var result = await coordinator.RunAsync(request, async message =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (message.RequestId != activeRequest) return;
                    Transcript.Text += message.Kind == EventKind.Text ? message.Message :
                        $"\n[{message.Kind}{(message.ToolName is null ? "" : " · " + message.ToolName)}] {message.Message}\n";
                    ConversationScroll.ScrollToEnd();
                });
            }, running.Token);
            // Cancelled/revoked turn results are never promoted into the UI report state.
            if (result.Code == "ok" && session == turnSession && turnSession.IsCurrent(request.Permissions))
            {
                latestResult = result.ToolResults.LastOrDefault(t => t.Success && t.Query is not null && QueryResultRetention.IsFresh(t.Query))?.Query;
                var proposed = result.ToolResults.LastOrDefault(t => t.Success && t.ToolName == DashboardProposals.Definition.Name);
                if (proposed is not null && workspace.Current.Model.Identity == request.Permissions.Target)
                    latestProposal = DashboardProposals.Accept(proposed, workspace.Current.Model);
            }
            if (latestResult is not null)
            {
                ResultDetails.Text = $"{(latestResult.Source.IsSynthetic ? "合成示範資料" : "已授權的查詢資料")}\n來源：{latestResult.Source.Name}\n版本：{latestResult.Source.Revision}\n結果：{latestResult.ResultId}\n\n" +
                    string.Join("\n", latestResult.Rows.Select(r => string.Join(" · ", r.Select(c => c.ToString())))) +
                    $"\n\nDAX：\n{latestResult.Query}\n\n暫存有效至：{latestResult.ExpiresAt.ToLocalTime():HH:mm:ss}（最長5分鐘）";
                ExportButton.IsEnabled = true;
                if (mode == ConversationMode.Report)
                {
                    var report = ReportSpecs.Default(latestResult);
                    HtmlReportRenderer.Render(latestResult, report);
                    ResultDetails.Text += $"\n\n報告規格已驗證：{report.Visuals.Length}個視覺；另行同意後可匯出。";
                }
            }
            if (latestProposal is not null)
                ResultDetails.Text += $"\n\nAI Dashboard提案：{latestProposal.Plan.Title}\n{latestProposal.Plan.Metrics.Length}個指標 · {latestProposal.Plan.Visuals.Length}個視覺\n尚未寫入或授予寫入權限。點選「檢視AI Dashboard提案」後檢查DAX與差異。";
            TryHistory(() => historyStore.RecordTurn(modeSnapshot.Mode, sourceKind, result.Code == "ok" ? HistoryOutcome.Success :
                result.Code == "cancelled" ? HistoryOutcome.Cancelled : result.Code.Contains("denied") || result.Code is "invalid_mode" or "permission_changed" ?
                HistoryOutcome.Denied : HistoryOutcome.Failed, Math.Min(8, result.ToolResults.Length)));
            return result;
        }
        finally
        {
            running.Dispose();
            running = null;
            AllowExternalTools.IsChecked = false;
            if (session == turnSession && session.Snapshot.Allows(Capability.ExternalTool))
                session.Update(session.Snapshot.Target, session.Snapshot.Mode, session.Snapshot.Granted.Remove(Capability.ExternalTool));
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        var demo = !workspace.Current.IsOffline;
        var chat = workspace.Current.Source == ModelSourceKind.Synthetic || aiProfile is not null;
        var route = ModeController.Get(modes.Snapshot.Mode).Route;
        SendButton.IsEnabled = !busy && (route == ModeRoute.Conversation ? chat :
            !workspace.Current.IsOffline && (route == ModeRoute.Dashboard || latestResult is not null && QueryResultRetention.IsFresh(latestResult)));
        SendButton.Content = route == ModeRoute.Conversation ? "送出" : route == ModeRoute.HtmlFile ? "匯出 HTML" : route == ModeRoute.HtmlMeasure ? "預覽度量值" : "開啟計畫";
        ModePicker.IsEnabled = !busy;
        var imports = toolRegistry.Snapshot.Tools.Count(t => t.Origin == ToolOrigin.McpProcess);
        ImportedToolsLabel.Text = $"已明確匯入：{imports} 個外部工具";
        ClearImportsButton.IsEnabled = !busy && imports > 0;
        AllowExternalTools.IsEnabled = !busy && imports > 0 && route == ModeRoute.Conversation;
        AllowQuery.IsEnabled = !busy && demo && route == ModeRoute.Conversation;
        AllowExport.IsEnabled = !busy && demo && route is ModeRoute.Conversation or ModeRoute.HtmlFile;
        MessageInput.IsEnabled = !busy && chat && route == ModeRoute.Conversation;
        AiConfigButton.IsEnabled = !busy;
        AiDemoButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        HistoryButton.IsEnabled = !busy;
        ReportStylePicker.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        OpenFolderButton.IsEnabled = !busy;
        OpenExampleButton.IsEnabled = !busy;
        DemoButton.IsEnabled = !busy;
        ReloadButton.IsEnabled = !busy && (offlineFolder is not null || localModel is not null || cloudModel is not null);
        LocalButton.IsEnabled = !busy;
        CloudButton.IsEnabled = !busy;
        CloudSignOutButton.IsEnabled = !busy && cloudIdentity is not null;
        McpButton.IsEnabled = !busy;
        WriteButton.IsEnabled = !busy && !workspace.Current.IsOffline && !workspace.Current.Model.Tables.IsEmpty;
        DashboardButton.IsEnabled = !busy && !workspace.Current.IsOffline && workspace.Current.Model.Tables.Any(t => !t.Columns.IsEmpty);
        ExportButton.IsEnabled = !busy && demo && latestResult is not null && QueryResultRetention.IsFresh(latestResult) &&
            route is ModeRoute.Conversation or ModeRoute.HtmlFile;
        ProposalButton.IsEnabled = !busy && !workspace.Current.IsOffline && latestProposal is not null;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        latestProposal = null;
        ProposalButton.IsEnabled = false;
        if (session is not null) session.Update(session.Snapshot.Target, session.Snapshot.Mode, ImmutableHashSet<Capability>.Empty);
        running?.Cancel();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowImportedClose) return;
        activeRequest = Guid.Empty;
        if (session is not null) session.Update(session.Snapshot.Target, session.Snapshot.Mode, ImmutableHashSet<Capability>.Empty);
        running?.Cancel();
        if (!toolRegistry.Snapshot.Tools.Any(t => t.Binding is not null) && importedCleanup.IsCompleted) return;
        e.Cancel = true;
        if (importedClosePending) return;
        importedClosePending = true; ClearImportedTools();
        await Task.Yield();
        while (running is not null) await Task.Delay(50);
        await importedCleanup; allowImportedClose = true; Close();
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var operation = new ModeController(AssistantMode.HtmlFile, workspace.Current.Model.Identity);
        await ExportReportAsync(operation, operation.Snapshot, true);
    }

    private async Task<string?> ExportReportAsync(ModeController operation, ModeSnapshot modeSnapshot, bool openBrowser)
    {
        operation.EnsureRoute(modeSnapshot, ModeRoute.HtmlFile);
        ExpireResultIfNeeded();
        if (workspace.Current.IsOffline || running is not null || latestResult is null) return null;
        var exportResult = latestResult;
        if (AllowExport.IsChecked != true)
        {
            MessageBox.Show(this, "請先勾選「允許匯出本機 HTML」。", "匯出授權");
            return null;
        }
        try
        {
            session = new(workspace.Current.Model.Identity, ConversationMode.Report, [Capability.ArtifactWrite]);
            var exportSession = session;
            using var cancellation = new CancellationTokenSource();
            running = cancellation;
            SetBusy(true);
            try
            {
                var folder = Path.Combine(storageRoot, "reports");
                operation.Check(modeSnapshot, exportSession.Snapshot, exportSession, Capability.ArtifactWrite);
                var spec = ReportSpecs.Default(exportResult);
                if (ReportStylePicker.SelectedIndex == 1)
                    spec = spec with { Visuals = spec.Visuals.Select(v => v.Kind == VisualKind.BarChart ? v with { Kind = VisualKind.LineChart } : v).ToImmutableArray() };
                else if (ReportStylePicker.SelectedIndex == 2)
                    spec = spec with { Visuals = spec.Visuals.Where(v => v.Kind == VisualKind.Table).ToImmutableArray() };
                var permissions = exportSession.Snapshot;
                var path = await new HtmlReportExporter(folder, exportSession, modeGuard: () =>
                    operation.Check(modeSnapshot, permissions, exportSession, Capability.ArtifactWrite)).ExportAsync(exportResult, permissions, cancellation.Token, spec);
                TryHistory(() => historyStore.RecordArtifact(AssistantMode.HtmlFile, workspace.Current.Source, path));
                ResultDetails.Text += $"\n\n已匯出：{path}";
                if (openBrowser) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return path;
            }
            finally
            {
                running = null;
                AllowExport.IsChecked = false;
                session?.Update(session.Snapshot.Target, session.Snapshot.Mode, []);
                SetBusy(false);
            }
        }
        catch { MessageBox.Show(this, "無法匯出或開啟報告。", "報告匯出"); }
        return null;
    }

    private async Task OpenHtmlMeasureAsync(ModeController operation, ModeSnapshot modeSnapshot)
    {
        operation.EnsureRoute(modeSnapshot, ModeRoute.HtmlMeasure);
        ExpireResultIfNeeded();
        if (running is not null || workspace.Current.IsOffline || latestResult is null) return;
        var source = latestResult;
        if (source.Source != workspace.Current.Model.Identity) throw new DefinitionConflictException();
        var table = SelectLocal("選取HTML度量值的目標表；下一步仍需預覽與寫入同意", workspace.Current.Model.Tables, t => t.Name);
        if (table is null) return;
        var writer = cloudModel is not null ? cloudModel.CreateWriter() : localModel is not null ? localModel.CreateWriter() : demoWriter ??= new DemoDefinitionWriter();
        var change = DashboardPlans.HtmlMeasure(table.Name, "SA_ReportHTML", source, ReportSpecs.Default(source));
        var window = new ModelWriteWindow(this, writer, source.Source, table.Name, [change], expectedSource: source.Source,
            sourceResult: source, modeGuard: () => operation.EnsureRoute(modeSnapshot, ModeRoute.HtmlMeasure), storageRoot: storageRoot);
        window.ShowDialog();
        if (!window.Attempted) return;
        InvalidateConversation();
        if (cloudModel is not null) await LoadCloudAsync(cloudModel);
        else if (localModel is not null) await LoadLocalAsync(localModel);
        else { await workspace.LoadAsync(demoWriter!, ModelSourceKind.Synthetic, default); PresentSelection(); }
    }

    public async Task RunSmokeAsync()
    {
        var probe = new ProviderIdentity("vault-smoke-" + Guid.NewGuid().ToString("N"), "https://example.invalid/chat/completions", "synthetic");
        try
        {
            vault.SaveSecret(probe, "synthetic-credential-only");
            if (vault.ReadSecret(probe) != "synthetic-credential-only") throw new InvalidOperationException("憑證往返失敗。");
        }
        finally { vault.DeleteSecret(probe); }
        AllowQuery.IsChecked = false;
        MessageInput.Text = "分析合成數值";
        var denied = await RunTurnAsync();
        if (denied.Code != "ok" || latestResult is not null || denied.ToolResults.Any(t => t.ToolName == "execute_dax_query") ||
            !Transcript.Text.Contains("目前未授權數值查詢"))
            throw new InvalidOperationException("WPF 未授權流程不符。");
        AllowQuery.IsChecked = true;
        var outcome = await RunTurnAsync();
        if (outcome.Code != "ok" || latestResult?.Rows.Length != 3 || !Transcript.Text.Contains("ToolCompleted"))
            throw new InvalidOperationException("WPF 示範流程未完成。");
        AllowExport.IsChecked = true;
        ExpireResultIfNeeded(new SyntheticResultExpiryClock());
        if (latestResult is not null || ExportButton.IsEnabled || AllowExport.IsChecked == true || AllowQuery.IsChecked == true || ResultDetails.Text.Contains("120000"))
            throw new InvalidOperationException("Expired WPF result retained rows or export consent.");
        AllowQuery.IsChecked = true;
        outcome = await RunTurnAsync();
        if (outcome.Code != "ok" || latestResult?.Rows.Length != 3 || !ExportButton.IsEnabled)
            throw new InvalidOperationException("Fresh WPF query after expiry failed.");
    }

    private void ExpireResultIfNeeded(TimeProvider? clock = null)
    {
        if (latestResult is null || QueryResultRetention.IsFresh(latestResult, clock)) return;
        latestResult = null;
        ExportButton.IsEnabled = false; AllowExport.IsChecked = false; AllowQuery.IsChecked = false;
        if (session is not null) session.Update(session.Snapshot.Target, session.Snapshot.Mode,
            session.Snapshot.Granted.Remove(Capability.DataQuery).Remove(Capability.ArtifactWrite));
        ResultDetails.Text = "查詢結果已過期，暫存數值已清除。請重新同意查詢後取得新結果。";
    }

    public async Task RunOfflineSmokeAsync()
    {
        await RunSmokeAsync();
        var folder = Path.Combine(AppContext.BaseDirectory, "Examples", "definition");
        if (!await LoadFolderAsync(folder)) throw new InvalidOperationException("無法載入內附 TMDL。");
        if (workspace.Current.Model.Tables.Length != 2 || workspace.Current.Model.Relationships.Length != 1 ||
            SendButton.IsEnabled || AllowQuery.IsEnabled || ExportButton.IsEnabled || latestResult is not null || session is not null)
            throw new InvalidOperationException("離線模型／權限狀態錯誤。");
        SearchInput.Text = "Total";
        var measure = FindTreeItem("table/SyntheticSales/measure/Total") ?? throw new InvalidOperationException("找不到度量值。");
        measure.IsSelected = true;
        if (!MetadataDetails.Text.Contains("SUM('SyntheticSales'[Amount])")) throw new InvalidOperationException("DAX 詳情未呈現。");
        SearchInput.Clear();
        var relationship = FindTreeItem("relationship/SyntheticLink") ?? throw new InvalidOperationException("找不到關係。");
        relationship.IsSelected = true;
        if (!MetadataDetails.Text.Contains("SyntheticDates[Date]")) throw new InvalidOperationException("關係詳情未呈現。");
        var prior = workspace.Current.Model;
        if (await LoadFolderAsync(Path.Combine(folder, "nonexistent-synthetic-folder")) || workspace.Current.Model != prior)
            throw new InvalidOperationException("讀取失敗未保留原模型。");
        if (!await LoadFolderAsync(folder)) throw new InvalidOperationException("重新讀取失敗。");
        OnReturnToDemo(this, new());
        if (workspace.Current.IsOffline || !SendButton.IsEnabled || latestResult is not null)
            throw new InvalidOperationException("返回示範的狀態錯誤。");
        if (!await LoadFolderAsync(folder)) throw new InvalidOperationException("重新選取離線模型失敗。");
        FindTreeItem("table/SyntheticSales/measure/Total")!.IsSelected = true;
    }

    private TreeViewItem? FindTreeItem(string id)
    {
        static TreeViewItem? Find(ItemCollection items, string target)
        {
            foreach (TreeViewItem item in items)
            {
                if (item.Tag is MetadataNode node && node.Id == target) return item;
                var child = Find(item.Items, target);
                if (child is not null)
                {
                    item.IsExpanded = true;
                    return child;
                }
            }
            return null;
        }
        return Find(ModelTree.Items, id);
    }
}
