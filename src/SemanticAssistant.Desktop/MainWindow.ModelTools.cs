using System.Collections.Immutable;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Desktop;

public partial class MainWindow
{
    private async void OnReviewAiWrite(object sender, RoutedEventArgs e)
    {
        try { await ReviewAiWriteAsync(); }
        catch { SourceStatus.Text = "AI提案已失效；請重新提出需求，沒有自動重試。"; }
    }
    private async Task ReviewAiWriteAsync()
    {
        if (running is not null || pendingAiWrite is null || pendingAiWriter is null) return;
        var pending = pendingAiWrite; var writer = pendingAiWriter; var state = modes.Snapshot;
        var provider = aiProfile;
        void Guard()
        {
            modes.EnsureConversation(state);
            if (workspace.Current.Model.Identity != pending.Source) throw new DefinitionConflictException();
        }
        pending.EnsureValid(modificationPolicy); Guard();
        var dialog = new AiWriteReviewWindow(this, pending, writer, modificationPolicy, dashboardRegistry, storageRoot, Guard);
        dialog.ShowDialog();
        if (!dialog.Attempted) { SourceStatus.Text = dialog.CompletionMessage; SetBusy(false); return; }
        pendingAiWrite = null; pendingAiWriter = null;
        await RefreshAfterModelOperationAsync(dialog.Outcome, dialog.Deployment, dialog.CompletionMessage, provider);
    }
    private async Task RefreshAfterModelOperationAsync(WriteOutcome? outcome, DashboardRecord? deployment, string receipt, ProviderIdentity? provider)
    {
        var previousMessages = Transcript.Messages;
        InvalidateConversation();
        bool loaded;
        if (cloudModel is not null) loaded = await LoadCloudAsync(cloudModel);
        else if (localModel is not null) loaded = await LoadLocalAsync(localModel);
        else
        {
            await workspace.LoadAsync(demoWriter ??= new DemoDefinitionWriter(), ModelSourceKind.Synthetic, default);
            PresentSelection(); loaded = true;
        }
        Transcript.RestoreMessages(previousMessages);
        AppendExecutionLog("[本機模型修改結果]\n" + receipt);
        ResultDetails.Text = receipt;
        ShowConversation();
        if (loaded && outcome?.Success == true && outcome.After?.Identity == workspace.Current.Model.Identity && provider == aiProfile)
        {
            DashboardProposal? proposal = deployment is null ? null : DashboardProposals.Accept(new("verified-deployment", DashboardProposals.Definition.Name,
                true, "verified", "本機回讀驗證的看板計畫", DashboardProposals.PlanPayload(deployment.Plan)), workspace.Current.Model);
            var context = receipt;
            if (deployment is not null) context += $"\n已部署Dashboard識別碼：{deployment.Id}\n登記版本：{deployment.Revision}\nHTML度量值：{deployment.HtmlMeasure.Table}[{deployment.HtmlMeasure.Name}]";
            conversation.SeedVerifiedChange(workspace.Current.Model.Identity, provider, context, proposal);
            // A deployed plan is read with get_dashboard_definition, not shown as an uncommitted proposal.
            latestProposal = null;
            SourceStatus.Text = "模型修改已回讀驗證 · 已切到新版本，可繼續追問或讀回已部署Dashboard";
        }
        else SourceStatus.Text = outcome?.Success == true
            ? "修改曾回讀成功，但來源已再次變更；請重新讀回定義，不沿用舊授權。"
            : "操作結果需核對；當次提案與授權已清除，沒有自動重試。";
        SetBusy(false);
    }

    private async void OnDeployedDashboards(object sender, RoutedEventArgs e)
    {
        if (running is not null || workspace.Current.IsOffline) return;
        try
        {
            IModelDefinitionWriter writer = cloudModel is not null ? cloudModel.CreateWriter() : localModel is not null ? localModel.CreateWriter() : demoWriter ??= new DemoDefinitionWriter();
            DefinitionState state;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                running = cancellation; SetBusy(true);
                try { state = await writer.ReadDefinitionAsync(cancellation.Token).WaitAsync(cancellation.Token); }
                finally { running = null; SetBusy(false); }
            }
            if (state.Identity != workspace.Current.Model.Identity)
            {
                if (cloudModel is not null) await LoadCloudAsync(cloudModel);
                else if (localModel is not null) await LoadLocalAsync(localModel);
                else { InvalidateConversation(); await workspace.LoadAsync(demoWriter!, ModelSourceKind.Synthetic, default); PresentSelection(); }
                if (state.Identity != workspace.Current.Model.Identity) throw new DefinitionConflictException();
            }
            var records = dashboardRegistry.List(state);
            var window = new Window { Owner = this, Title = "已部署 Dashboard · 定義與更新", Width = 850, Height = 680, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = new ScrollViewer { Content = panel };
            panel.Children.Add(new TextBlock { Text = records.IsEmpty ? "目前模型沒有可核對的Dashboard部署紀錄。舊版無登記的物件不能只靠名稱自動接管。"
                : "選取看板讀回定義。所有權或內容不符的項目不能更新；由部署意圖恢復的項目仍須完整回讀一致。", TextWrapping = TextWrapping.Wrap });
            var list = new ListBox { MinHeight = 120, MaxHeight = 200, ItemsSource = records.Select(r => new DashboardView(r,
                $"{r.Plan.Title} · {(DashboardRegistry.Matches(r, state) ? r.RecoveredFromIntent ? "回讀一致／登記待重建" : "回讀一致" : "版本／所有權衝突")} · {r.Id}")).ToArray(), DisplayMemberPath = "Label" };
            var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 300, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var update = new Button { Content = "填入此看板的更新需求", IsEnabled = false, Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(list); panel.Children.Add(detail); panel.Children.Add(update);
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not DashboardView view) return;
                detail.Text = JsonSerializer.Serialize(new { DashboardId = view.Record.Id, view.Record.Revision, Plan = DashboardProposals.PlanPayload(view.Record.Plan),
                    view.Record.HtmlMeasure, view.Record.Appearance }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                update.IsEnabled = DashboardRegistry.Matches(view.Record, state);
            };
            update.Click += (_, _) =>
            {
                if (list.SelectedItem is not DashboardView view || !DashboardRegistry.Matches(view.Record, state)) return;
                ModePicker.SelectedIndex = 4;
                FillPrompt($"請先讀回 Dashboard {view.Record.Id} 的目前定義，再依照以下需求更新：");
                window.Close();
            };
            window.ShowDialog();
        }
        catch { SourceStatus.Text = "無法讀取Dashboard部署紀錄；原檔保留，沒有修改模型。"; }
    }
    private sealed record DashboardView(DashboardRecord Record, string Label);
}
