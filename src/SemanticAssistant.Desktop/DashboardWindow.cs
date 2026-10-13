using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Desktop;

internal sealed class DashboardWindow : Window
{
    private readonly IModelDefinitionWriter writer;
    private readonly ISemanticModelReader reader;
    private readonly PermissionSession session;
    private readonly DashboardCoordinator coordinator;
    private readonly Action? modeGuard;
    private readonly Action<string>? artifactRecorded;
    private readonly TextBox input = new() { AcceptsReturn = true, Height = 210, MaxLength = 32000, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox previewText = new() { IsReadOnly = true, Height = 180, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox status = new() { IsReadOnly = true, MinHeight = 85, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox htmlName = new() { Text = "SA_DashboardHTML", MaxLength = 128 };
    private readonly CheckBox allowWrite = new() { Content = "同意本次預覽的模型修改", IsEnabled = false },
        allowQuery = new() { Content = "同意執行預覽所列的數值查詢", IsEnabled = false },
        allowExport = new() { Content = "同意將這份查詢結果匯出本機 HTML", IsEnabled = false };
    private readonly Button prepare = new() { Content = "1. 檢查計畫並預覽" }, apply = new() { Content = "2. 執行已同意的修改" },
        query = new() { Content = "3. 執行已同意的查詢" }, export = new() { Content = "4. 匯出本機 HTML" },
        htmlMeasure = new() { Content = "預覽固定快照 HTML 度量值" },
        dynamicHtmlMeasure = new() { Content = "預覽動態 HTML 度量值（依篩選重算）" }, cancel = new() { Content = "取消目前操作", IsEnabled = false };
    private DashboardDraft? draft;
    private WriteOutcome? outcome;
    private QueryResult? result;
    private readonly DispatcherTimer resultExpiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ReportSpec? report;
    private CancellationTokenSource? running;
    private bool closeRequested;
    private readonly ModelIdentity? proposedSource;
    private readonly string storageRoot;
    public bool Attempted { get; private set; }
    public string? ExportPath { get; private set; }

    public DashboardWindow(Window owner, ISemanticModelReader reader, IModelDefinitionWriter writer,
        IReadOnlyDaxQueryRunner runner, ModelSnapshot model, DashboardProposal? initialProposal = null, QueryLimits? queryLimits = null,
        Action? modeGuard = null, Action<string>? artifactRecorded = null, string? storageRoot = null)
    {
        this.storageRoot = storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemanticAssistant");
        this.artifactRecorded = artifactRecorded;
        this.modeGuard = modeGuard; modeGuard?.Invoke();
        this.writer = writer; this.reader = reader; Owner = owner; Title = "Dashboard · 計畫、修改與報告"; Width = 960; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        session = new(model.Identity, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead));
        coordinator = new(reader, writer, runner, session, new FileModelWriteJournal(Path.Combine(this.storageRoot, "write-journal")), queryLimits: queryLimits);
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = $"{model.Identity.Name}\n{model.Identity.Id}\n模型修改、資料查詢與檔案匯出分別同意。此流程沒有 AI 外送。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "計畫：最多六個自有指標、一個分組維度及可選日期範圍。視覺欄位 0 為分組，其後依指標順序。固定 HTML 保存查詢快照；動態 HTML 由目前已驗證指標產生 DAX，依篩選重新計算前100組，KPI只聚合顯示組別。動態 DAX最多8000字，標籤最多256字，需支援HTML/SVG的Power BI視覺；實際引擎與切片器驗收尚待完成。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        if (initialProposal is not null) DashboardProposals.Validate(initialProposal, model);
        proposedSource = initialProposal?.Source;
        var seed = initialProposal?.Plan ?? (model.Identity.IsSynthetic ? DemoDashboard.Plan : Seed(model));
        if (initialProposal is not null) panel.Children.Add(new TextBlock
        {
            Text = "AI提案（尚未驗證DAX語意與所有權）· 請檢視指標、週期與視覺，再按檢查計畫。\n來源版本：" + initialProposal.Source.Revision + "\n提案雜湊：" + initialProposal.Hash,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8)
        });
        // Plain TextBox only: keep Chinese object names and DAX readable during human review.
        input.Text = JsonSerializer.Serialize(seed, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        });
        panel.Children.Add(input); panel.Children.Add(prepare); panel.Children.Add(previewText); panel.Children.Add(allowWrite); panel.Children.Add(apply);
        panel.Children.Add(allowQuery); panel.Children.Add(query); panel.Children.Add(allowExport); panel.Children.Add(export);
        panel.Children.Add(new TextBlock { Text = "HTML 度量值名稱（點選後仍須獨立預覽與同意）", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(htmlName); panel.Children.Add(htmlMeasure); panel.Children.Add(dynamicHtmlMeasure); panel.Children.Add(cancel); panel.Children.Add(status);
        input.TextChanged += (_, _) => ResetPlan();
        prepare.Click += async (_, _) => await PrepareAsync(); apply.Click += async (_, _) => await ApplyAsync();
        query.Click += async (_, _) => await QueryAsync(); export.Click += async (_, _) => await ExportAsync();
        htmlMeasure.Click += async (_, _) => await HtmlMeasureAsync(); cancel.Click += (_, _) => Cancel();
        dynamicHtmlMeasure.Click += async (_, _) => await DynamicHtmlMeasureAsync();
        Closing += (_, e) => { if (running is null) return; e.Cancel = true; closeRequested = true; Cancel(); status.Text = "正在取消並等待完成；完成後關閉。"; };
        SetBusy(false);
        resultExpiryTimer.Tick += (_, _) => ExpireResultIfNeeded();
        resultExpiryTimer.Start();
        Closed += (_, _) => { resultExpiryTimer.Stop(); result = null; report = null; };
    }
    private static DashboardPlan Seed(ModelSnapshot model)
    {
        var table = model.Tables.First(t => !t.Columns.IsEmpty);
        return new(1, "分析 Dashboard", new(table.Name, table.Columns[0].Name), null,
            [new(model.Tables[0].Name, "SA_新指標", "1")], [new("table", "查詢資料", VisualKind.Table, null, null, null)]);
    }
    private void ResetPlan()
    {
        draft = null; outcome = null; result = null; report = null; ExportPath = null;
        allowWrite.IsChecked = allowQuery.IsChecked = allowExport.IsChecked = false; SetBusy(running is not null);
    }
    private void UpdateGrants(params Capability[] grants) => session.Update(session.Snapshot.Target, ConversationMode.Report, grants.ToImmutableHashSet());
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (running is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2)); running = cancellation; SetBusy(true);
        try { modeGuard?.Invoke(); await action(cancellation.Token); modeGuard?.Invoke(); }
        catch { status.Text = "操作未能完成：請核對計畫、來源版本及結果。不會自動重試；已成功的模型修改不會因後續查詢／匯出失敗而自動刪除。"; }
        finally
        {
            UpdateGrants(Capability.MetadataRead); running = null; SetBusy(false); if (closeRequested) Close();
        }
    }
    private Task PrepareAsync() => RunAsync(async token =>
    {
        ResetPlan(); UpdateGrants(Capability.MetadataRead);
        var current = await writer.ReadDefinitionAsync(token).WaitAsync(token);
        if (current.Identity.Id != session.Snapshot.Target.Id) throw new DefinitionConflictException();
        if (proposedSource is not null && current.Identity != proposedSource) throw new DefinitionConflictException();
        session.Update(current.Identity, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead));
        draft = await coordinator.PrepareAsync(DashboardPlans.Parse(input.Text), session.Snapshot, token);
        previewText.Text = draft.Definition.Diff + $"\n\n待另行同意的查詢：\n{draft.Query}\n\n計畫雜湊：{draft.Hash}";
        status.Text = "預覽完成；尚未修改模型或查詢數值。";
    });
    private Task ApplyAsync()
    {
        if (draft is null || allowWrite.IsChecked != true) { status.Text = "未同意本次模型修改；沒有寫入。"; return Task.CompletedTask; }
        return RunAsync(async token =>
        {
            UpdateGrants(Capability.MetadataRead, Capability.DefinitionWrite);
            try
            {
                var permissions = session.Snapshot; var approval = coordinator.ApproveAfterUserConfirmation(draft, permissions); Attempted = true;
                outcome = await coordinator.ApplyAsync(draft, approval, permissions, token);
                if (outcome.After is { } after) session.Update(after.Identity, ConversationMode.Report, ImmutableHashSet.Create(Capability.MetadataRead));
                status.Text = $"{outcome.Code}\n{outcome.Message}\n操作：{draft.Definition.Plan.OperationId}\n部分失敗：{outcome.PartialFailure}";
            }
            finally { allowWrite.IsChecked = false; }
        });
    }
    private Task QueryAsync()
    {
        if (draft is null || outcome?.Success != true || allowQuery.IsChecked != true)
        { status.Text = "模型修改尚未驗證成功或未同意查詢；沒有查詢數值。"; return Task.CompletedTask; }
        return RunAsync(async token =>
        {
            result = null; report = null; allowExport.IsChecked = false; UpdateGrants(Capability.MetadataRead, Capability.DataQuery);
            try
            {
                result = await coordinator.QueryAsync(draft, outcome, session.Snapshot, token);
                QueryResultRetention.EnsureFresh(result);
                report = DashboardPlans.Report(draft.Plan, result);
                status.Text = $"受限查詢完成：{result.Rows.Length} 列，截斷={result.Truncated}\n結果：{result.ResultId}\n暫存有效至：{result.ExpiresAt.ToLocalTime():HH:mm:ss}（最長5分鐘）\n{(result.Source.IsSynthetic ? "合成示範數值。" : "目前授權模型的查詢結果。")}";
            }
            finally { allowQuery.IsChecked = false; }
        });
    }
    private async Task EnsureCurrentResultAsync(CancellationToken token)
    {
        if (result is null || report is null) throw new InvalidDataException("缺少查詢結果。");
        var pinned = result;
        QueryResultRetention.EnsureFresh(pinned);
        var current = await writer.ReadDefinitionAsync(token).WaitAsync(token);
        QueryResultRetention.EnsureFresh(pinned);
        if (result != pinned || current.Identity != pinned.Source || current.Identity != session.Snapshot.Target) throw new DefinitionConflictException();
    }
    private Task ExportAsync()
    {
        ExpireResultIfNeeded();
        if (result is null || report is null || allowExport.IsChecked != true)
        { status.Text = "尚未同意匯出這份查詢結果。"; return Task.CompletedTask; }
        return RunAsync(async token =>
        {
            try
            {
                await EnsureCurrentResultAsync(token); UpdateGrants(Capability.MetadataRead, Capability.ArtifactWrite);
                ExportPath = await new HtmlReportExporter(Path.Combine(storageRoot, "reports"), session, modeGuard: modeGuard).ExportAsync(result!, session.Snapshot, token, report);
                status.Text = $"本機 HTML 已匯出：\n{ExportPath}";
                try { artifactRecorded?.Invoke(ExportPath); } catch { status.Text += "\n歷史索引保存失敗；報告已完成。"; }
            }
            finally { allowExport.IsChecked = false; }
        });
    }
    private Task HtmlMeasureAsync() => RunAsync(async token =>
    {
        await EnsureCurrentResultAsync(token);
        var change = DashboardPlans.HtmlMeasure(draft!.Plan.Metrics[0].Table, htmlName.Text, result!, report!);
        var dialog = new ModelWriteWindow(this, writer, session.Snapshot.Target, change.Table, [change], expectedSource: session.Snapshot.Target, sourceResult: result, modeGuard: modeGuard, storageRoot: storageRoot);
        dialog.ShowDialog(); Attempted |= dialog.Attempted;
        if (dialog.Attempted) ResetPlan();
    });
    private async Task<MeasureChange> PrepareDynamicHtmlAsync(CancellationToken token)
    {
        if (draft is null || outcome?.Success != true) throw new InvalidDataException("需先驗證本計畫的指標寫入。");
        var permissions = session.Snapshot;
        var current = await writer.ReadDefinitionAsync(token).WaitAsync(token);
        if (!session.IsCurrent(permissions) || current.Identity != permissions.Target ||
            !DefinitionRules.MatchesApplied(draft.Definition.Plan, current)) throw new DefinitionConflictException();
        var model = await reader.ReadSnapshotAsync(token).WaitAsync(token);
        if (!session.IsCurrent(permissions) || model.Identity != current.Identity) throw new DefinitionConflictException();
        return DynamicHtmlMeasureCompiler.Compile(draft.Plan, model, draft.Plan.Metrics[0].Table, htmlName.Text);
    }
    private Task DynamicHtmlMeasureAsync() => RunAsync(async token =>
    {
        var change = await PrepareDynamicHtmlAsync(token);
        var dialog = new ModelWriteWindow(this, writer, session.Snapshot.Target, change.Table, [change], expectedSource: session.Snapshot.Target, modeGuard: modeGuard, storageRoot: storageRoot);
        dialog.ShowDialog(); Attempted |= dialog.Attempted;
        if (dialog.Attempted) ResetPlan();
    });
    private void Cancel() { UpdateGrants(); running?.Cancel(); }
    private void SetBusy(bool busy)
    {
        input.IsEnabled = prepare.IsEnabled = !busy; cancel.IsEnabled = busy;
        allowWrite.IsEnabled = apply.IsEnabled = !busy && draft is not null && outcome is null;
        allowQuery.IsEnabled = query.IsEnabled = !busy && outcome?.Success == true;
        allowExport.IsEnabled = export.IsEnabled = htmlMeasure.IsEnabled = !busy && result is not null && report is not null && QueryResultRetention.IsFresh(result);
        dynamicHtmlMeasure.IsEnabled = !busy && outcome?.Success == true;
        htmlName.IsEnabled = !busy;
    }
    internal async Task RunSyntheticSmokeAsync(string? previewPath = null)
    {
        await PrepareAsync(); if (draft is null) throw new InvalidOperationException("Dashboard preview failed.");
        await ApplyAsync(); if (Attempted) throw new InvalidOperationException("Write without consent.");
        allowWrite.IsChecked = true; await ApplyAsync(); if (outcome?.Success != true) throw new InvalidOperationException("Dashboard write failed.");
        var dynamicChange = await PrepareDynamicHtmlAsync(default);
        if (!dynamicChange.Expression.Contains("SUMMARIZECOLUMNS") || result is not null || !dynamicHtmlMeasure.IsEnabled)
            throw new InvalidOperationException("Dynamic HTML must be available without reading data rows.");
        await QueryAsync(); if (result is not null) throw new InvalidOperationException("Query without consent.");
        allowQuery.IsChecked = true; await QueryAsync(); if (result?.Rows.Length != 3) throw new InvalidOperationException("Dashboard query failed.");
        if (previewPath is not null) SavePreview(previewPath);
        allowExport.IsChecked = true;
        ExpireResultIfNeeded(new SyntheticResultExpiryClock());
        if (result is not null || report is not null || allowExport.IsChecked == true || export.IsEnabled || htmlMeasure.IsEnabled || !dynamicHtmlMeasure.IsEnabled)
            throw new InvalidOperationException("Expired Dashboard result retained report or snapshot actions.");
        await ExportAsync(); if (ExportPath is not null) throw new InvalidOperationException("Expired Dashboard exported.");
        allowQuery.IsChecked = true; await QueryAsync();
        if (result?.Rows.Length != 3) throw new InvalidOperationException("Fresh Dashboard query after expiry failed.");
        var fixedChange = DashboardPlans.HtmlMeasure(draft!.Plan.Metrics[0].Table, "SA_SnapshotExpiryFixture", result, report!);
        var fixedWindow = new ModelWriteWindow(this, writer, session.Snapshot.Target, fixedChange.Table, [fixedChange],
            expectedSource: session.Snapshot.Target, sourceResult: result);
        fixedWindow.Show();
        try { await fixedWindow.RunExpiredSourceSmokeAsync(); }
        finally { fixedWindow.Close(); }
        await ExportAsync(); if (ExportPath is not null) throw new InvalidOperationException("Export without consent.");
        allowExport.IsChecked = true; await ExportAsync();
        if (ExportPath is null || !File.ReadAllText(ExportPath).Contains("397,000.00") || allowWrite.IsChecked == true || allowQuery.IsChecked == true || allowExport.IsChecked == true)
            throw new InvalidOperationException("Dashboard export/consent failed.");
        var dynamicWriter = new ModelWriteWindow(this, writer, session.Snapshot.Target, dynamicChange.Table, [dynamicChange], expectedSource: session.Snapshot.Target);
        dynamicWriter.Show();
        try { await dynamicWriter.RunSyntheticSmokeAsync(); }
        finally { dynamicWriter.Close(); }
        if (!dynamicWriter.Attempted || dynamicWriter.LastOutcome?.Success != true) throw new InvalidOperationException("Dynamic HTML write failed.");
        var staleWriter = new ModelWriteWindow(this, writer, session.Snapshot.Target, dynamicChange.Table, [dynamicChange], expectedSource: session.Snapshot.Target);
        staleWriter.Show();
        try { await staleWriter.RunStalePreviewSmokeAsync(); }
        finally { staleWriter.Close(); }
        Attempted |= dynamicWriter.Attempted; ResetPlan();
        if (draft is not null || result is not null || report is not null || allowExport.IsChecked == true)
            throw new InvalidOperationException("Dynamic write retained old result/plan.");
    }
    private void SavePreview(string path)
    {
        UpdateLayout();
        var panel = (FrameworkElement)((ScrollViewer)Content).Content;
        var width = (int)Math.Ceiling(panel.ActualWidth); var height = (int)Math.Ceiling(panel.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private void ExpireResultIfNeeded(TimeProvider? clock = null)
    {
        if (result is null || QueryResultRetention.IsFresh(result, clock)) return;
        result = null; report = null; allowExport.IsChecked = false;
        UpdateGrants(Capability.MetadataRead);
        status.Text = "查詢結果已過期；請另行同意重新查詢。固定快照與匯出已停用，已驗證的指標保持不變。";
        SetBusy(running is not null);
    }
}
