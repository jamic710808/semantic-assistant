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
    private readonly ConversationSession conversation = new();
    private readonly DashboardRegistry dashboardRegistry;
    private PendingAiWrite? pendingAiWrite;
    private IModelDefinitionWriter? pendingAiWriter;
    private readonly ToolRegistry toolRegistry = new(ToolDispatcher.Definitions);
    private Task importedCleanup = Task.CompletedTask;
    private bool allowImportedClose;
    private bool importedClosePending;
    private readonly ModeController modes = new(AssistantMode.Analyze, DemoModel.Snapshot.Identity);
    private string? offlineFolder;
    private bool uiReady;
    private ProviderIdentity? aiProfile;
    private AssistantSettings settings = AssistantSettings.Default;
    private readonly ModelModificationPolicy modificationPolicy = new();
    internal ModelModificationPolicy ModificationPolicy => modificationPolicy;
    internal void EnableSyntheticModificationFixture()
    {
        if (!workspace.Current.Model.Identity.IsSynthetic || localModel is not null || cloudModel is not null)
            throw new InvalidOperationException("合成驗收政策不能套用到真實模型。");
        settings = settings with { AllowModelModification = true };
        modificationPolicy.Update(true);
    }
    private readonly IAssistantSettingsStore settingsStore;
    private readonly IHistoryStore historyStore;
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
        dashboardRegistry = new(Path.Combine(this.storageRoot, "dashboard-registry"));
        this.historyStore = historyStore ?? new FileHistoryStore(Path.Combine(this.storageRoot, "history"), Path.Combine(this.storageRoot, "reports"));
        this.settingsStore = settingsStore ?? new FileAssistantSettingsStore(Path.Combine(this.storageRoot, "preferences"));
        InitializeComponent();
        uiReady = true;
        modificationPolicy.Update(settings.AllowModelModification);
        PresentSelection();
        if (restoreSettings) RestoreSettings();
        resultExpiryTimer.Tick += (_, _) => { ExpireResultIfNeeded(); if (running is null) SetBusy(false); };
        resultExpiryTimer.Start();
        Closed += (_, _) => { resultExpiryTimer.Stop(); latestResult = null; conversation.Reset(); ReleaseCloud(); };
    }

    private void PresentSelection()
    {
        var current = workspace.Current;
        var model = current.Model;
        if (modes.Snapshot.Target != model.Identity) modes.Change(modes.Snapshot.Mode, model.Identity);
        ModelName.Text = DisplayModelName;
        ModelCounts.Text = $"{model.Tables.Length} 表 · {model.Tables.Sum(t => t.Columns.Length)} 欄位\n" +
            $"{model.Tables.Sum(t => t.Measures.Length)} 度量值 · {model.Relationships.Length} 單欄關係";
        SourceStatus.Text = current.Source == ModelSourceKind.Cloud ? "已連接 Power BI 雲端 · 已讀取模型結構" :
            current.Source == ModelSourceKind.Desktop ? "已連接 Power BI Desktop · 已讀取模型結構" :
            current.IsOffline ? "已載入離線 TMDL · 本機結構檢視" : "已載入合成示範模型 · 本機示範";
        PolicyHint.Text = current.IsOffline ? "離線來源不提供數值查詢。設定 AI 後，按送出即以目前供應商分析模型結構。" :
            current.Source is ModelSourceKind.Desktop or ModelSourceKind.Cloud ? "按送出即使用目前 AI 分析；數值查詢依執行設定啟用。取消可停止本次回合。" : "目前為合成示範；取消會停止當次回合。";
        DetailTitle.Text = "模型概覽";
        MetadataDetails.Text = DisplayModelSummary();
        ResultTitle.Text = current.IsOffline ? "離線檔案來源" : "查詢結果與來源";
        ResultDetails.Text = current.IsOffline ? $"來源類型：TMDL 定義目錄\n\n{offlineFolder}\n\n內容版本：\n{model.Identity.Revision}\n\n只讀取檔案中的結構。\n未啟動 Power BI 連線。\n未讀取事實資料列。\n\n選取左側物件查看型別、DAX 或關係。" : "尚未執行數值查詢。";
        MessageInput.Clear();
        ChatTab.IsEnabled = true;
        ContentTabs.SelectedIndex = 0;
        ShowWelcome();
        BuildModelTree();
        ApplyDefaultPermissions();
        SetBusy(running is not null);
    }

    private void BuildModelTree()
    {
        ModelTree.Items.Clear();
        var nodes = MetadataInspector.Build(workspace.Current.Model, SearchInput.Text);
        if (!nodes.IsEmpty)
        {
            var model = workspace.Current.Model;
            var rootNode = new MetadataNode("overview", DisplayModelName, DisplayModelSummary(), []);
            var root = new TreeViewItem { Header = DisplayModelName, Tag = rootNode, IsExpanded = true, ToolTip = DisplayModelName };
            var expand = !string.IsNullOrWhiteSpace(SearchInput.Text);
            var tables = nodes.FirstOrDefault(n => n.Id == "tables");
            if (tables is not null) root.Items.Add(ToTreeItem(tables, expand));
            var measures = tables?.Children.SelectMany(t => t.Children.Where(c => c.Id.EndsWith("/measures", StringComparison.Ordinal))
                .SelectMany(c => c.Children)).ToImmutableArray() ?? [];
            if (!expand || !measures.IsEmpty)
                root.Items.Add(ToTreeItem(new("measures", $"度量值（{measures.Length}）", "模型中的度量值原始定義；尚未執行。", measures), expand));
            var relationships = nodes.FirstOrDefault(n => n.Id == "relationships");
            if (relationships is not null) root.Items.Add(ToTreeItem(relationships, expand));
            ModelTree.Items.Add(root);
        }
        if (nodes.IsEmpty)
        {
            DetailTitle.Text = "沒有符合的物件";
            MetadataDetails.Text = "請調整搜尋字詞。搜尋只比對物件名稱，不修改模型。";
        }
    }

    private static TreeViewItem ToTreeItem(MetadataNode node, bool expand)
    {
        var item = new TreeViewItem { Header = node.Title, Tag = node, IsExpanded = expand || node.Id == "tables" };
        item.FontWeight = node.Id is "tables" or "measures" or "relationships" ? FontWeights.SemiBold : FontWeights.Normal;
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
        await SelectDesktopReportAsync(startup: false);
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
        conversation.Reset();
        pendingAiWrite = null; pendingAiWriter = null;
        ClearImportedTools();
        modes.Change(modes.Snapshot.Mode, workspace.Current.Model.Identity);
        if (session is not null)
            session.Update(session.Snapshot.Target, session.Snapshot.Mode, ImmutableHashSet<Capability>.Empty);
        session = null;
        activeRequest = Guid.Empty;
        latestResult = null;
        latestProposal = null;
        ProposalButton.IsEnabled = false;
        ApplyDefaultPermissions();
        ExportButton.IsEnabled = false;
        Transcript.ClearMessages();
        ExecutionDetails.Clear();
        ExecutionLogPanel.IsExpanded = false;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = modes.Snapshot;
            switch (ModeController.Get(snapshot.Mode).Route)
            {
                case ModeRoute.HtmlMeasure: await OpenHtmlMeasureAsync(modes, snapshot); break;
                default:
                    var outcome = await RunTurnAsync();
                    if (outcome.Code != "ok") break;
                    if (pendingAiWrite is not null) { await ReviewAiWriteAsync(); break; }
                    if (snapshot.Mode == AssistantMode.HtmlFile && AllowExport.IsChecked == true)
                        await ExportReportAsync(modes, snapshot, true);
                    if (snapshot.Mode == AssistantMode.Dashboard && latestProposal is not null &&
                        outcome.ToolResults.Any(r => r.Success && r.ToolName == DashboardProposals.Definition.Name))
                        await OpenDashboardAsync(latestProposal, () => modes.EnsureRoute(snapshot, ModeRoute.Dashboard));
                    break;
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
        session = null; activeRequest = Guid.Empty; running?.Cancel();
        ApplyDefaultPermissions();
        SourceStatus.Text = "模式已切換；同一來源的對話與產物保留，當次操作同意已撤銷。";
        SetBusy(running is not null);
    }

    private void OnOpenMcp(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        new McpToolsWindow(this, workspace.Current.Model.Identity,
            modes.Snapshot.Mode != AssistantMode.HtmlMeasure ? ImportExternalTool : null, modificationPolicy).ShowDialog();
        SetBusy(false);
    }
    private void ImportExternalTool(McpAiToolBinding binding)
    {
        if (binding.Descriptor.Target != workspace.Current.Model.Identity || running is not null) throw new ToolPolicyException("target_changed");
        toolRegistry.ImportAfterUserSelection(binding);
        session?.Update(session.Snapshot.Target, session.Snapshot.Mode, []);
        session = null; ApplyDefaultPermissions();
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
        var bindings = toolRegistry.ClearExternal(); ApplyDefaultPermissions();
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
        var window = new ModelWriteWindow(this, writer, workspace.Current.Model.Identity, workspace.Current.Model.Tables[0].Name, storageRoot: storageRoot,
            modificationPolicy: modificationPolicy);
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
            artifactRecorded: path => TryHistory(() => historyStore.RecordArtifact(AssistantMode.Dashboard, workspace.Current.Source, path)), storageRoot: storageRoot,
            appearance: settings.Templates.For(ReportOutputKind.Dashboard), modificationPolicy: modificationPolicy, registry: dashboardRegistry,
            evidence: conversation.LatestReport?.Evidence ?? []); dialog.ShowDialog();
        if (!dialog.Attempted) return;
        await RefreshAfterModelOperationAsync(dialog.LastOutcome, dialog.LastDeployment,
            dialog.LastOutcome?.Message ?? "Dashboard操作已嘗試，請重新核對模型。", aiProfile);
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
        try { new AiProfilesWindow(this, settings, vault, ApplyAiSettings).ShowDialog(); }
        catch { SourceStatus.Text = "無法開啟 AI 模型設定；目前設定仍保留。"; }
    }

    private void ApplyAiSettings(AssistantSettings candidate, bool activeCredentialChanged)
    {
        candidate.Validate();
        settingsStore.Save(candidate);
        var changed = aiProfile != candidate.AiProvider || activeCredentialChanged;
        if (changed) InvalidateConversation();
        settings = candidate;
        aiProfile = candidate.AiProvider;
        UpdateAiLabel();
        if (changed) PresentSelection();
        else SetBusy(false);
        SourceStatus.Text = changed ? "AI 模型設定已保存並套用；可以直接送出提問。" : "AI 模型清單已保存。";
    }

    private void UpdateAiLabel()
    {
        var display = settings.AiProfiles.FirstOrDefault(p => p.Identity == aiProfile)?.DisplayName;
        AiProviderLabel.Text = aiProfile is null ? "AI：本機模擬" : $"AI：{display ?? aiProfile.Model}";
        AiProviderLabel.ToolTip = aiProfile is null ? "本機模擬 AI" : $"{aiProfile.Model}\n{aiProfile.Endpoint}";
    }

    private void OnAiDemo(object sender, RoutedEventArgs e)
    {
        try { ApplyAiSettings(settings with { AiProvider = null }, false); }
        catch { SourceStatus.Text = "切換本機模擬 AI 未保存；目前設定仍保留。"; }
    }

    private void RestoreSettings()
    {
        var loaded = settingsStore.Load(); settings = loaded.Settings.IncludeCurrentProfile();
        if (!settings.FullAccessDefaultsApplied)
        {
            settings = settings with { AllowModelModification = true, FullAccessDefaultsApplied = true };
            if (loaded.Status is SettingsLoadStatus.Loaded or SettingsLoadStatus.Migrated or SettingsLoadStatus.Missing)
            {
                try { settingsStore.Save(settings); }
                catch { /* Apply requested defaults to this window even if persistence is unavailable. */ }
            }
        }
        modificationPolicy.Update(settings.AllowModelModification);
        aiProfile = settings.AiProvider;
        UpdateAiLabel();
        PresentSelection();
        if (loaded.Status is SettingsLoadStatus.Invalid or SettingsLoadStatus.Unsupported or SettingsLoadStatus.Unavailable)
            SourceStatus.Text = "本機設定無法載入；使用預設示範並保留原設定檔。";
    }

    private void PersistSettings()
    {
        try { settingsStore.Save(settings); SourceStatus.Text = "本機設定已保存；可以繼續提問。"; }
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
        modes.EnsureConversation(modeSnapshot);
        if (running is not null) throw new InvalidOperationException("回合正在執行。");
        pendingAiWrite = null; pendingAiWriter = null;
        if (workspace.Current.Source != ModelSourceKind.Synthetic && aiProfile is null) return new(Guid.NewGuid(), "inspection_only", []);
        var providerIdentity = providerOverride?.Identity ?? aiProfile;
        conversation.Bind(workspace.Current.Model.Identity, providerIdentity);
        var consent = aiProfile is null ? null : AiTurnConsent.ForSend(aiProfile);
        latestResult = null;
        latestProposal = conversation.LatestDashboard;
        ProposalButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ResultDetails.Text = "尚未執行數值查詢。";
        var grants = ImmutableHashSet.Create(Capability.MetadataRead);
        if (!workspace.Current.IsOffline && AllowQuery.IsChecked == true) grants = grants.Add(Capability.DataQuery);
        if (AllowExternalTools.IsChecked == true && toolRegistry.Snapshot.Tools.Any(t => t.Binding?.IsCurrent == true)) grants = grants.Add(Capability.ExternalTool);
        if (!workspace.Current.IsOffline && modificationPolicy.Snapshot.Allowed && modes.Allows(modeSnapshot, Capability.DefinitionWrite))
            grants = grants.Add(Capability.DefinitionWrite);
        var mode = ModeController.Get(modeSnapshot.Mode).PermissionMode;
        session = new(workspace.Current.Model.Identity, mode, grants, consent);
        var turnSession = session;
        var model = new DemoModel();
        ISemanticModelReader reader = cloudModel is not null ? cloudModel : localModel is not null ? localModel : workspace.Current.IsOffline ? new TmdlModelReader(offlineFolder!) : demoWriter is not null ? demoWriter : model;
        IReadOnlyDaxQueryRunner queryRunner = cloudModel is not null ? cloudModel : localModel is not null ? localModel : demoWriter is not null ? demoWriter : model;
        using var external = aiProfile is null ? null : new OpenAiCompatibleProvider(aiProfile, vault);
        IAiProvider provider = providerOverride ?? (external is null ? new DemoAiProvider() : external);
        IModelDefinitionWriter? modelWriter = workspace.Current.IsOffline ? null : cloudModel is not null ? cloudModel.CreateWriter() :
            localModel is not null ? localModel.CreateWriter() : demoWriter ??= new DemoDefinitionWriter();
        var modelOperations = modelWriter is null ? null : new AiModelOperations(reader, modelWriter, dashboardRegistry,
            modificationPolicy, settings.Templates.For(ReportOutputKind.Dashboard));
        var queryLimits = settings.Budgets.QueryLimits(cloudModel is not null);
        var coordinator = new ConversationCoordinator(reader, provider, new ToolDispatcher(reader, queryRunner, session, queryLimits,
            modes: modes, modeSnapshot: modeSnapshot, toolRegistry: toolRegistry,
            externalConfirmation: confirmationOverride ?? ((preview, token) => AiDialogs.ConfirmExternalAsync(this, preview, token)),
            modificationPolicy: modificationPolicy, aiOperations: modelOperations), session,
            settings.Budgets.TurnLimits(), new ModelContextBuilder(budget: new(settings.Budgets.ContextTables, settings.Budgets.ContextCharacters), displayName: DisplayModelName),
            modes: modes, modeSnapshot: modeSnapshot);
        running = new CancellationTokenSource();
        activeRequest = Guid.NewGuid();
        var history = conversation.Context(consent?.Categories, out var omittedTurns);
        var request = new ConversationRequest(activeRequest, MessageInput.Text, mode, session.Snapshot)
        { Intent = ModeController.Intent(modeSnapshot.Mode), History = history };
        ExecutionDetails.Clear();
        ExecutionLogPanel.IsExpanded = false;
        if (consent is not null) AppendExecutionLog($"當次外送授權範圍：{string.Join("、", consent.Categories)}");
        SourceStatus.Text = "正在思考…";
        Transcript.AppendUserMessage(request.Message);
        if (omittedTurns > 0) AppendExecutionLog($"[脈絡] {omittedTurns} 個舊回合因資料到期、外送範圍或脈絡預算，未交給 AI。");
        ShowConversation();
        ContentTabs.SelectedIndex = 0;
        SetBusy(true);
        try
        {
            var result = await coordinator.RunAsync(request, async message =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (message.RequestId != activeRequest) return;
                    // Text events may be intermediate narration before another tool call.
                    // Publish only the accepted final answer below, never execution chatter.
                    if (message.Kind != EventKind.Text)
                        AppendExecutionLog($"[{message.Kind}{(message.ToolName is null ? "" : " · " + message.ToolName)}{(message.Code is null ? "" : " · " + message.Code)}] {message.Message}");
                    if (message.Kind is EventKind.Error or EventKind.Cancelled) SourceStatus.Text = message.Message;
                    else if (message.Kind == EventKind.Completed) SourceStatus.Text = "正在整理回答…";
                });
            }, running.Token);
            // Cancelled/revoked turn results are never promoted into the UI report state.
            if (result.Code == "ok" && session == turnSession && turnSession.IsCurrent(request.Permissions))
            {
                latestResult = result.ToolResults.LastOrDefault(t => t.Success && t.Query is not null && QueryResultRetention.IsFresh(t.Query))?.Query;
                var proposed = result.ToolResults.LastOrDefault(t => t.Success && t.ToolName == DashboardProposals.Definition.Name);
                if (proposed is not null && workspace.Current.Model.Identity == request.Permissions.Target)
                    latestProposal = DashboardProposals.Accept(proposed, workspace.Current.Model);
                var evidence = request.History.SelectMany(m => m.Evidence).Concat(result.ToolResults)
                    .Where(r => r.Query is not null || r.ToolName.StartsWith("mcp_", StringComparison.Ordinal)).Distinct().ToImmutableArray();
                pendingAiWrite = modelOperations?.Seal(evidence);
                pendingAiWriter = pendingAiWrite is null ? null : modelWriter;
                var answer = result.Answer;
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    conversation.Complete(request, providerIdentity, answer, result.ToolResults,
                        proposed is null ? null : latestProposal,
                        consent is null || consent.Categories.Contains(EgressCategory.Metadata),
                        grants.Contains(Capability.ExternalTool) && (consent is null || consent.Categories.Contains(EgressCategory.ExternalToolData)));
                    Transcript.AppendAssistantMessage(answer);
                    Transcript.ShowLatestAnswer(ConversationScroll);
                    ResultDetails.Text = $"報告文件：{conversation.LatestReport!.Id}\n模式：{ModeController.Get(modeSnapshot.Mode).Label}\n\n{answer}";
                    SourceStatus.Text = "回答完成 · 可以繼續提問";
                    if (modeSnapshot.Mode == AssistantMode.Dashboard && proposed is null)
                        SourceStatus.Text = "AI 已回答，但此回合尚未提出有效 Dashboard 計畫；可補充需求後追問。";
                }
                else SourceStatus.Text = "AI 未提供完整回答，請重試或補充問題。";
            }
            if (latestResult is not null)
            {
                ResultDetails.Text += $"\n\n{(latestResult.Source.IsSynthetic ? "合成示範資料" : "已授權的查詢資料")}\n來源：{DisplayModelName}\n版本：{latestResult.Source.Revision}\n結果：{latestResult.ResultId}\n\n" +
                    string.Join("\n", latestResult.Rows.Select(r => string.Join(" · ", r.Select(c => c.ToString())))) +
                    $"\n\nDAX：\n{latestResult.Query}\n\n暫存有效至：{latestResult.ExpiresAt.ToLocalTime():HH:mm:ss}（最長5分鐘）";
                ExportButton.IsEnabled = true;
                if (mode == ConversationMode.Report)
                {
                    var report = ReportSpecs.Default(latestResult);
                    HtmlReportRenderer.Render(latestResult, report, appearance: settings.Templates.For(ReportOutputKind.ReportMeasure));
                    ResultDetails.Text += $"\n\n報告規格已驗證：{report.Visuals.Length}個視覺；另行同意後可匯出。";
                }
            }
            if (latestProposal is not null)
                ResultDetails.Text += $"\n\nAI Dashboard提案：{latestProposal.Plan.Title}\n{latestProposal.Plan.Metrics.Length}個指標 · {latestProposal.Plan.Visuals.Length}個視覺\n尚未寫入或授予寫入權限。點選「檢視AI Dashboard提案」後檢查DAX與差異。";
            if (pendingAiWrite is not null)
            {
                ResultDetails.Text += "\n\nAI修改提案待本機確認；尚未寫入。";
                SourceStatus.Text = "AI 已提出模型修改；下一步顯示實際差異，需同意後寫入與回讀。";
            }
            TryHistory(() => historyStore.RecordTurn(modeSnapshot.Mode, sourceKind, result.Code == "ok" ? HistoryOutcome.Success :
                result.Code == "cancelled" ? HistoryOutcome.Cancelled : result.Code.Contains("denied") || result.Code is "invalid_mode" or "permission_changed" ?
                HistoryOutcome.Denied : HistoryOutcome.Failed, Math.Min(8, result.ToolResults.Length)));
            return result;
        }
        finally
        {
            running.Dispose();
            running = null;
            ApplyDefaultPermissions();
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
        var naturalLanguage = route != ModeRoute.HtmlMeasure;
        SendButton.IsEnabled = !busy && (naturalLanguage ? chat && !string.IsNullOrWhiteSpace(MessageInput.Text) :
            demo && modificationPolicy.Snapshot.Allowed && latestResult is not null && QueryResultRetention.IsFresh(latestResult));
        SendButton.Content = naturalLanguage ? "送出" : "預覽度量值";
        ModePicker.IsEnabled = !busy;
        var imports = toolRegistry.Snapshot.Tools.Count(t => t.Origin == ToolOrigin.McpProcess);
        ImportedToolsLabel.Text = $"已明確匯入：{imports} 個外部工具";
        ClearImportsButton.IsEnabled = !busy && imports > 0;
        AllowExternalTools.IsEnabled = !busy && modificationPolicy.Snapshot.Allowed && imports > 0 && naturalLanguage;
        AllowQuery.IsEnabled = !busy && demo && naturalLanguage;
        AllowExport.IsEnabled = !busy;
        MessageInput.IsEnabled = !busy && naturalLanguage;
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
        WriteButton.IsEnabled = !busy && modificationPolicy.Snapshot.Allowed && !workspace.Current.IsOffline && !workspace.Current.Model.Tables.IsEmpty;
        WriteButton.ToolTip = modificationPolicy.Snapshot.Allowed ? "讀取與預覽模型修改" : "安全模式：請先到權限管理允許修改模型。";
        DashboardButton.IsEnabled = !busy && !workspace.Current.IsOffline && workspace.Current.Model.Tables.Any(t => !t.Columns.IsEmpty);
        ExportButton.IsEnabled = !busy && (conversation.LatestReport?.IsFresh == true ||
            demo && latestResult is not null && QueryResultRetention.IsFresh(latestResult));
        ProposalButton.IsEnabled = !busy && !workspace.Current.IsOffline && latestProposal is not null;
        ReportArtifactButton.Visibility = conversation.LatestReport is null ? Visibility.Collapsed : Visibility.Visible;
        ReportArtifactButton.IsEnabled = ExportButton.IsEnabled;
        DashboardArtifactButton.Visibility = latestProposal is null ? Visibility.Collapsed : Visibility.Visible;
        DashboardArtifactButton.IsEnabled = ProposalButton.IsEnabled;
        AiModificationArtifactButton.Visibility = pendingAiWrite is null ? Visibility.Collapsed : Visibility.Visible;
        AiModificationArtifactButton.IsEnabled = !busy && pendingAiWrite is not null && modificationPolicy.Snapshot.Allowed;
        DeployedDashboardsButton.IsEnabled = !busy && !workspace.Current.IsOffline;
        CopyAnswerButton.IsEnabled = !busy && conversation.LatestReport is not null;
        CopyConversationButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(Transcript.Text);
        SyncShellState(busy);
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
        var document = conversation.LatestReport;
        if (running is not null || document is null && (workspace.Current.IsOffline || latestResult is null)) return null;
        var exportResult = latestResult;
        if (AllowExport.IsChecked != true)
        {
            ShowUtility(0, "執行設定");
            SourceStatus.Text = "報告已產生；請勾選「允許匯出本機 HTML」後再按匯出。";
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
                var visualResult = document?.Query ?? (document is null ? exportResult : null);
                var spec = visualResult is null ? null : ReportSpecs.Default(visualResult);
                if (spec is not null && ReportStylePicker.SelectedIndex == 1)
                    spec = spec with { Visuals = spec.Visuals.Select(v => v.Kind == VisualKind.BarChart ? v with { Kind = VisualKind.LineChart } : v).ToImmutableArray() };
                else if (spec is not null && ReportStylePicker.SelectedIndex == 2)
                    spec = spec with { Visuals = spec.Visuals.Where(v => v.Kind == VisualKind.Table).ToImmutableArray() };
                var permissions = exportSession.Snapshot;
                var exporter = new HtmlReportExporter(folder, exportSession, modeGuard: () =>
                    operation.Check(modeSnapshot, permissions, exportSession, Capability.ArtifactWrite));
                var path = document is not null
                    ? await exporter.ExportDocumentAsync(document, permissions, cancellation.Token, settings.Templates.For(ReportOutputKind.HtmlFile), spec)
                    : await exporter.ExportAsync(exportResult!, permissions, cancellation.Token, spec, settings.Templates.For(ReportOutputKind.HtmlFile));
                TryHistory(() => historyStore.RecordArtifact(AssistantMode.HtmlFile, workspace.Current.Source, path));
                ResultDetails.Text += $"\n\n已匯出：{path}";
                if (openBrowser) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return path;
            }
            finally
            {
                running = null;
                ApplyDefaultPermissions();
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
        var change = DashboardPlans.HtmlMeasure(table.Name, "SA_ReportHTML", source, ReportSpecs.Default(source),
            appearance: settings.Templates.For(ReportOutputKind.ReportMeasure));
        var window = new ModelWriteWindow(this, writer, source.Source, table.Name, [change], expectedSource: source.Source,
            sourceResult: source, modeGuard: () => operation.EnsureRoute(modeSnapshot, ModeRoute.HtmlMeasure), storageRoot: storageRoot,
            modificationPolicy: modificationPolicy);
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
            !ExecutionDetails.Text.Contains("目前未授權數值查詢"))
            throw new InvalidOperationException("WPF 未授權流程不符。");
        AllowQuery.IsChecked = true;
        var outcome = await RunTurnAsync();
        if (outcome.Code != "ok" || latestResult?.Rows.Length != 3 || !ExecutionDetails.Text.Contains("ToolCompleted"))
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
        ExportButton.IsEnabled = false; ApplyDefaultPermissions();
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
