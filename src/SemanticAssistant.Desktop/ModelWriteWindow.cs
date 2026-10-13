using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using SemanticAssistant.Adapters.Demo;

namespace SemanticAssistant.Desktop;

internal sealed class ModelWriteWindow : Window
{
    private readonly IModelDefinitionWriter writer;
    private readonly ModelIdentity? expectedSource;
    private readonly Action? modeGuard;
    private QueryResult? sourceResult;
    private bool sourceExpired;
    private readonly DispatcherTimer sourceExpiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PermissionSession session;
    private readonly ModelWriteCoordinator coordinator;
    private readonly TextBox input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 150,
        MaxLength = 128000
    };
    private readonly TextBox diff = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 220
    };
    private readonly TextBox status = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 100 };
    private readonly ComboBox inputMode = new() { ItemsSource = new[] { "度量值陣列", "混合模型物件 JSON" }, SelectedIndex = 0 };
    private readonly CheckBox approve = new() { Content = "我同意對上述模型、版本與修改參數執行這一次寫入", IsEnabled = false };
    private readonly Button prepare = new() { Content = "讀取完整定義並預覽" }, apply = new() { Content = "執行已同意的修改", IsEnabled = false },
        cancel = new() { Content = "取消", IsEnabled = false };
    private DefinitionPreview? preview;
    private CancellationTokenSource? running;
    private bool closeRequested;
    public WriteOutcome? LastOutcome { get; private set; }
    public bool Attempted { get; private set; }

    public ModelWriteWindow(Window owner, IModelDefinitionWriter writer, ModelIdentity target, string table,
        ImmutableArray<MeasureChange>? initialChanges = null, ModelChanges? initialObjects = null, ModelIdentity? expectedSource = null,
        QueryResult? sourceResult = null, Action? modeGuard = null, string? storageRoot = null)
    {
        this.modeGuard = modeGuard; modeGuard?.Invoke();
        if (sourceResult is not null)
        {
            QueryResultRetention.EnsureFresh(sourceResult);
            if (sourceResult.Source != target) throw new DefinitionConflictException();
        }
        this.sourceResult = sourceResult;
        this.writer = writer;
        this.expectedSource = expectedSource;
        if (target.IsSynthetic) inputMode.ItemsSource = new[] { "度量值陣列", "混合模型物件 JSON", "載入合成混合範例" };
        session = new(target, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead));
        coordinator = new(writer, session, new FileModelWriteJournal(System.IO.Path.Combine(storageRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemanticAssistant"), "write-journal")));
        Owner = owner; Title = "模型寫入 · 預覽與逐次同意"; Width = 850; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = $"目標：{target.Name}\n來源：{target.Id}\n{(target.IsSynthetic ? "合成示範，只改記憶體中的自製模型。" : "真實語意模型。需對此模型有寫入權限；雲端還需 XMLA read-write／容量及管理條件。")}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "支援自有度量值、計算欄位與單欄關係，合計最多16個；不覆寫無所有權標記的既有物件。真實計算欄位會要求引擎重算；示範只驗證結構，不計算新欄位資料。關係方向／基數影響篩選。雲端寫入同意後可能需另授權 Dataset.ReadWrite.All；還原僅用快取的授權。寫入失敗嘗試還原本次自有物件；外部 XMLA 不保證原子交易。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        input.Text = JsonSerializer.Serialize(initialChanges ?? [new MeasureChange(table, "SA_新指標", "1")], new JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        panel.Children.Add(inputMode); panel.Children.Add(input); panel.Children.Add(prepare); panel.Children.Add(diff); panel.Children.Add(approve);
        inputMode.SelectionChanged += (_, _) =>
        {
            object template = inputMode.SelectedIndex == 2 ? DemoModelChanges.Mixed : inputMode.SelectedIndex == 0 ? new[] { new MeasureChange(table, "SA_新指標", "1") } :
                new ModelChanges([], [new(table, "SA_新計算欄位", "1", ColumnValueType.Int64)], []);
            input.Text = JsonSerializer.Serialize(template, DisplayOptions());
        };
        if (initialObjects is not null) { inputMode.SelectedIndex = 1; input.Text = JsonSerializer.Serialize(initialObjects, DisplayOptions()); }
        panel.Children.Add(apply); panel.Children.Add(cancel); panel.Children.Add(status);
        input.TextChanged += (_, _) => ClearPreview();
        prepare.Click += async (_, _) => await PrepareAsync(); apply.Click += async (_, _) => await ApplyAsync(); cancel.Click += (_, _) => Cancel();
        Closing += (_, e) =>
        {
            if (running is null) return;
            e.Cancel = true; closeRequested = true; Cancel();
            status.Text = "正在取消並等待寫入／還原結束，完成後會關閉。";
        };
        sourceExpiryTimer.Tick += (_, _) => ExpireSourceIfNeeded();
        if (sourceResult is not null) sourceExpiryTimer.Start();
        Closed += (_, _) => { sourceExpiryTimer.Stop(); this.sourceResult = null; input.Clear(); diff.Clear(); };
    }
    private async Task PrepareAsync()
    {
        ExpireSourceIfNeeded();
        if (running is not null || sourceExpired) return;
        ClearPreview(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)); running = cancellation; SetBusy(true);
        try
        {
            LimitToSourceLifetime(cancellation, TimeSpan.FromSeconds(20));
            modeGuard?.Invoke();
            var changes = inputMode.SelectedIndex == 0 ? new ModelChanges(StrictJson.Parse<ImmutableArray<MeasureChange>>(input.Text, 128_000, 8), [], []) :
                StrictJson.Parse<ModelChanges>(input.Text, 128_000, 12);
            var current = await writer.ReadDefinitionAsync(cancellation.Token).WaitAsync(cancellation.Token);
            if (current.Identity.Id != session.Snapshot.Target.Id) throw new DefinitionConflictException();
            if (expectedSource is not null && current.Identity != expectedSource) throw new DefinitionConflictException();
            session.Update(current.Identity, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead));
            preview = await coordinator.PrepareAsync(changes, session.Snapshot, cancellation.Token);
            modeGuard?.Invoke();
            if (sourceResult is not null) QueryResultRetention.EnsureFresh(sourceResult);
            cancellation.Token.ThrowIfCancellationRequested();
            diff.Text = preview.Diff + $"\n參數雜湊：{preview.PlanHash}\n\n此預覽沒有執行寫入。";
            approve.IsEnabled = true; status.Text = "請確認目標與差異，再勾選當次寫入同意。";
        }
        catch { ClearPreview(); status.Text = "預覽失敗：請檢查 JSON、表／物件名稱、版本與所有權。沒有執行寫入。"; }
        finally { running = null; SetBusy(false); if (closeRequested) Close(); }
    }
    private async Task ApplyAsync()
    {
        ExpireSourceIfNeeded();
        if (running is not null || sourceExpired || preview is null || approve.IsChecked != true)
        { status.Text = "尚未完成當次預覽與同意；沒有寫入。"; return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2)); running = cancellation; SetBusy(true);
        var writePreview = preview;
        LastOutcome = null;
        try
        {
            LimitToSourceLifetime(cancellation, TimeSpan.FromMinutes(2));
            modeGuard?.Invoke();
            session.Update(writePreview.Plan.Target, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead, Capability.DefinitionWrite));
            var permissions = session.Snapshot;
            var approval = coordinator.ApproveAfterUserConfirmation(writePreview, permissions); Attempted = true;
            LastOutcome = await coordinator.ApplyAsync(writePreview, approval, permissions, cancellation.Token);
            status.Text = $"{LastOutcome.Code}\n{LastOutcome.Message}\n操作：{writePreview.Plan.OperationId}\n部分失敗：{LastOutcome.PartialFailure}";
        }
        catch { status.Text = "寫入未能完成。若操作已開始，請重新讀取並核對模型；不要直接重試。"; }
        finally
        {
            session.Update(LastOutcome?.After?.Identity ?? session.Snapshot.Target, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.MetadataRead));
            running = null; ClearPreview(); SetBusy(false); if (closeRequested) Close();
        }
    }
    private void Cancel()
    {
        session.Update(session.Snapshot.Target, session.Snapshot.Mode, ImmutableHashSet<Capability>.Empty); running?.Cancel();
    }
    private void ClearPreview() { preview = null; approve.IsChecked = false; approve.IsEnabled = false; apply.IsEnabled = false; }
    private void SetBusy(bool busy)
    { inputMode.IsEnabled = input.IsEnabled = !busy && !sourceExpired; prepare.IsEnabled = !busy && !sourceExpired; apply.IsEnabled = !busy && !sourceExpired && preview is not null; cancel.IsEnabled = busy; }

    private void LimitToSourceLifetime(CancellationTokenSource cancellation, TimeSpan operationLimit)
    {
        if (sourceResult is null) return;
        QueryResultRetention.EnsureFresh(sourceResult);
        var remaining = QueryResultRetention.Remaining(sourceResult);
        cancellation.CancelAfter(remaining < operationLimit ? remaining : operationLimit);
    }

    private void ExpireSourceIfNeeded(TimeProvider? clock = null)
    {
        if (sourceResult is null || QueryResultRetention.IsFresh(sourceResult, clock)) return;
        sourceExpired = true; sourceResult = null; sourceExpiryTimer.Stop();
        Cancel(); ClearPreview(); input.Clear(); diff.Clear(); SetBusy(running is not null);
        status.Text = "HTML快照的查詢結果已過期；預覽與同意已清除。請重新查詢並產生新預覽。";
    }

    internal async Task RunExpiredSourceSmokeAsync()
    {
        await PrepareAsync();
        if (preview is null || sourceResult is null) throw new InvalidOperationException("Missing snapshot preview.");
        approve.IsChecked = true; ExpireSourceIfNeeded(new SyntheticResultExpiryClock());
        await ApplyAsync();
        if (Attempted || preview is not null || sourceResult is not null || approve.IsChecked == true || input.Text.Length != 0 || prepare.IsEnabled)
            throw new InvalidOperationException("Expired snapshot reached write.");
    }
    private static JsonSerializerOptions DisplayOptions() => new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    internal async Task RunSyntheticSmokeAsync()
    {
        await PrepareAsync(); if (preview is null || approve.IsChecked == true) throw new InvalidOperationException("Writer preview failed.");
        await ApplyAsync(); if (Attempted) throw new InvalidOperationException("Write without confirmation.");
        approve.IsChecked = true; await ApplyAsync();
        if (LastOutcome?.Success != true || approve.IsChecked == true || preview is not null) throw new InvalidOperationException("Write/verify/consent failed.");
    }
    internal async Task RunStalePreviewSmokeAsync()
    {
        await PrepareAsync();
        if (preview is not null || approve.IsEnabled || Attempted || LastOutcome is not null)
            throw new InvalidOperationException("Stale generated measure reached write preview.");
    }
}
