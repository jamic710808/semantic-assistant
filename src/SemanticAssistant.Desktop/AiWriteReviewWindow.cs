using System.IO;
using System.Windows;
using System.Windows.Controls;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

// Read-only proposal and diff. The user approves exactly the issued preview, not
// an editable AI JSON payload which could drift from the Dashboard registry.
internal sealed class AiWriteReviewWindow : Window
{
    private readonly PendingAiWrite pending;
    private readonly ModelModificationPolicy policy;
    private readonly DashboardRegistry registry;
    private readonly Action guard;
    private readonly PermissionSession permissions;
    private readonly ModelWriteCoordinator coordinator;
    private readonly TextBox diff = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 260 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox consent = new() { Content = "我同意對此來源、版本與下列差異執行這一次模型修改", IsEnabled = false, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button apply = new() { Content = "執行已同意的修改", IsEnabled = false }, close = new() { Content = "關閉" };
    private DefinitionPreview? preview;
    private CancellationTokenSource? running;
    private bool closeRequested;
    public WriteOutcome? Outcome { get; private set; }
    public DashboardRecord? Deployment { get; private set; }
    public bool Attempted { get; private set; }
    public string CompletionMessage { get; private set; } = "提案未執行；沒有修改模型。";

    public AiWriteReviewWindow(Window owner, PendingAiWrite pending, IModelDefinitionWriter writer, ModelModificationPolicy policy,
        DashboardRegistry registry, string storageRoot, Action guard)
    {
        this.pending = pending; this.policy = policy; this.registry = registry; this.guard = guard;
        permissions = new(pending.Source, SemanticAssistant.Contracts.ConversationMode.Analyze, [SemanticAssistant.Contracts.Capability.MetadataRead]);
        coordinator = new(new ProposalBoundModelWriter(writer, EnsureCurrent), permissions, new FileModelWriteJournal(Path.Combine(storageRoot, "write-journal")), policy);
        Owner = owner; Title = pending.Dashboard is null ? "AI 模型修改 · 差異確認" : "AI Dashboard 部署／更新 · 差異確認";
        Width = 860; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = $"來源：{pending.Source.Name}\n版本：{pending.Source.Revision}\nAI 提案尚未寫入，請核對實際差異。只更新本程式自有物件。", TextWrapping = TextWrapping.Wrap });
        if (pending.Dashboard is { } dashboard) panel.Children.Add(new TextBlock
        { Text = $"\nDashboard：{dashboard.Plan.Title}\n識別碼：{dashboard.Id}\n{(dashboard.Previous is null ? "建立新看板" : "更新已回讀核對的看板")} · 指標與動態HTML一起寫入。未使用的舊指標保留，原生報表頁面／視覺容器不會自動建立。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(diff); panel.Children.Add(consent); panel.Children.Add(apply); panel.Children.Add(close); panel.Children.Add(status);
        apply.Click += async (_, _) => await ApplyAsync(); close.Click += (_, _) => Close();
        Loaded += async (_, _) => await PrepareAsync();
        Closing += (_, e) => { if (running is null) return; e.Cancel = true; closeRequested = true; Revoke(); status.Text = "正在取消並等待寫入／補償結束。"; };
        policy.Changed += OnPolicyChanged;
        Closed += (_, _) => { policy.Changed -= OnPolicyChanged; Revoke(); };
    }
    private void EnsureCurrent() { guard(); pending.EnsureValid(policy); }
    private async Task PrepareAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)); running = cancellation;
        try
        {
            EnsureCurrent(); preview = await coordinator.PrepareAsync(pending.Changes, permissions.Snapshot, cancellation.Token);
            EnsureCurrent(); diff.Text = preview.Diff + $"\n預覽雜湊：{preview.PlanHash}";
            consent.IsEnabled = apply.IsEnabled = true; status.Text = "已完成預覽；尚未寫入。";
        }
        catch { preview = null; status.Text = "來源、政策、資料效期或所有權已失效；沒有寫入。請重新提出需求。"; }
        finally { running = null; if (closeRequested) Close(); }
    }
    private async Task ApplyAsync()
    {
        if (running is not null || preview is null || consent.IsChecked != true || Attempted) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2)); running = cancellation;
        consent.IsEnabled = apply.IsEnabled = false;
        DashboardRegistry.RegistryLease? lease = null;
        try
        {
            EnsureCurrent();
            if (pending.Dashboard is { } registration) lease = registry.Begin(registration, preview);
            permissions.Update(pending.Source, SemanticAssistant.Contracts.ConversationMode.Analyze,
                [SemanticAssistant.Contracts.Capability.MetadataRead, SemanticAssistant.Contracts.Capability.DefinitionWrite]);
            var granted = permissions.Snapshot;
            var approval = coordinator.ApproveAfterUserConfirmation(preview, granted);
            EnsureCurrent(); Attempted = true;
            Outcome = await coordinator.ApplyAsync(preview, approval, granted, cancellation.Token);
            CompletionMessage = Outcome.Message + $"\n操作：{preview.Plan.OperationId} · 狀態：{Outcome.Code}";
            if (pending.Dashboard is { } dashboard) CompletionMessage += $"\n本次Dashboard識別碼：{dashboard.Id}";
            if (Outcome.Success && lease is not null)
            {
                try { Deployment = lease.Commit(Outcome); CompletionMessage += $"\nDashboard登記已完成：{Deployment.Id}\n登記版本：{Deployment.Revision}"; }
                catch { CompletionMessage += "\n模型修改已回讀成功，但Dashboard登記保存失敗；部署意圖保留。請重新列出看板核對恢復狀態，勿盲目重試建立。"; }
            }
            status.Text = CompletionMessage;
        }
        catch { CompletionMessage = Attempted ? "操作已開始但尚未取得完整結果；請重讀模型與部署紀錄，勿直接重試。" : "預覽、來源或登記版本已失效；沒有開始模型寫入。"; status.Text = CompletionMessage; }
        finally
        {
            lease?.Dispose(); permissions.Update(Outcome?.After?.Identity ?? pending.Source, permissions.Snapshot.Mode, []);
            running = null; consent.IsChecked = false; if (closeRequested) Close();
        }
    }
    private void Revoke() { permissions.Update(permissions.Snapshot.Target, permissions.Snapshot.Mode, []); running?.Cancel(); }
    private void OnPolicyChanged() => Dispatcher.BeginInvoke(new Action(() =>
    { Revoke(); preview = null; consent.IsChecked = false; consent.IsEnabled = apply.IsEnabled = false; status.Text = "修改政策已切換；舊提案與同意失效。"; }));
}
