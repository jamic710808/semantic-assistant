using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

internal sealed class HistoryWindow : Window
{
    private readonly IHistoryStore store;
    private readonly CheckBox summaries = new() { Content = "保存回合摘要（時間、模式、來源類型、狀態；不含對話內容）" };
    private readonly CheckBox artifacts = new() { Content = "保存已匯出報告的檔名索引（不含報告內容）" };
    private readonly TextBox days = new() { Width = 65, MaxLength = 2 }, maximum = new() { Width = 65, MaxLength = 3 };
    private readonly ListBox list = new() { MinHeight = 180, DisplayMemberPath = "Label" };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button save = new() { Content = "保存政策" }, remove = new() { Content = "移除此筆索引" }, clear = new() { Content = "清除全部摘要與索引" };
    private HistoryLoadResult loaded = new(HistorySnapshot.Empty, HistoryLoadStatus.Missing);

    public HistoryWindow(Window owner, IHistoryStore store)
    {
        Owner = owner; this.store = store; Title = "本機歷史 · 可選保存"; Width = 790; Height = 670;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = "預設關閉。只保存操作摘要與報告位置，不保存對話、DAX、資料列、模型名稱、帳號、key或操作同意。開啟紀錄不會恢復來源、查詢或權限。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(summaries); panel.Children.Add(artifacts);
        var bounds = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        bounds.Children.Add(new TextBlock { Text = "保留天數（1–30）：", VerticalAlignment = VerticalAlignment.Center }); bounds.Children.Add(days);
        bounds.Children.Add(new TextBlock { Text = "  最多筆數（1–100）：", VerticalAlignment = VerticalAlignment.Center }); bounds.Children.Add(maximum); bounds.Children.Add(save);
        panel.Children.Add(bounds); panel.Children.Add(new TextBlock { Text = "停用某類保存會清除該類摘要／索引。清除或到期只影響歷史索引；已匯出HTML檔案仍保留。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list); panel.Children.Add(detail);
        var buttons = new WrapPanel(); buttons.Children.Add(remove); buttons.Children.Add(clear);
        var refresh = new Button { Content = "重新讀取" }; buttons.Children.Add(refresh); panel.Children.Add(buttons); panel.Children.Add(status);
        save.Click += (_, _) => SavePolicy(); remove.Click += (_, _) => RemoveSelected(); refresh.Click += (_, _) => Refresh();
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "清除所有摘要與索引？已匯出的HTML檔案會保留。", "清除本機歷史", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                Mutate(() => store.Clear());
        };
        list.SelectionChanged += (_, _) => ShowSelection(); Refresh();
    }

    private void SavePolicy()
    {
        if (!int.TryParse(days.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var retention) ||
            !int.TryParse(maximum.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity))
        { status.Text = "請填入有效天數與筆數。"; return; }
        Mutate(() => store.SetPolicy(new(summaries.IsChecked == true, artifacts.IsChecked == true, retention, capacity)));
    }
    private void RemoveSelected()
    { if (list.SelectedItem is View selected) Mutate(() => store.Remove(selected.Entry.Id)); }
    private void Mutate(Action action)
    {
        try { action(); Refresh(); status.Text = "歷史保存政策／索引已更新；報告檔案與操作授權未變更。"; }
        catch { status.Text = "更新失敗；不覆寫無效、未知或鎖定中的歷史紀錄。"; }
    }
    private void Refresh()
    {
        loaded = store.Load(); var policy = loaded.Snapshot.Policy;
        summaries.IsChecked = policy.SaveSummaries; artifacts.IsChecked = policy.SaveArtifacts;
        days.Text = policy.RetentionDays.ToString(CultureInfo.InvariantCulture); maximum.Text = policy.MaximumEntries.ToString(CultureInfo.InvariantCulture);
        list.ItemsSource = loaded.Snapshot.Entries.Select(e => new View(e,
            $"{e.CreatedAt.ToLocalTime():MM/dd HH:mm:ss} · {ModeController.Get(e.Mode).Label} · {e.Source} · {(e.ArtifactFileName is null ? OutcomeLabel(e.Outcome) : "HTML報告索引")}")).ToArray();
        detail.Text = "選取紀錄查看摘要或報告位置。";
        var valid = loaded.Status is HistoryLoadStatus.Missing or HistoryLoadStatus.Loaded;
        save.IsEnabled = valid; remove.IsEnabled = clear.IsEnabled = valid && !loaded.Snapshot.Entries.IsEmpty;
        status.Text = valid ? $"目前 {loaded.Snapshot.Entries.Length} 筆；只顯示期限內紀錄，下一次更新整理實體索引。" : "歷史無法載入；原檔保留，保存／移除已停用。";
    }
    private void ShowSelection()
    {
        if (list.SelectedItem is not View selected) return;
        var e = selected.Entry;
        detail.Text = $"時間：{e.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n模式：{ModeController.Get(e.Mode).Label}\n來源類型：{e.Source}\n結果：{OutcomeLabel(e.Outcome)}\n工具次數：{e.ToolCalls}";
        if (e.ArtifactFileName is null) return;
        try { var path = store.ArtifactLocation(e); detail.Text += $"\n報告位置：{path}\n{(File.Exists(path) ? "檔案目前存在" : "檔案已移除或不可用")}"; }
        catch { detail.Text += "\n報告位置無法驗證。"; }
    }
    private static string OutcomeLabel(HistoryOutcome outcome) => outcome switch
    { HistoryOutcome.Success => "完成", HistoryOutcome.Denied => "拒絕／撤銷", HistoryOutcome.Cancelled => "已取消", _ => "失敗" };
    private sealed record View(HistoryEntry Entry, string Label);

    internal void EnableSyntheticSmoke()
    {
        summaries.IsChecked = artifacts.IsChecked = true; days.Text = "7"; maximum.Text = "3"; SavePolicy();
        if (!store.Load().Snapshot.Policy.SaveSummaries || !store.Load().Snapshot.Policy.SaveArtifacts) throw new InvalidOperationException("History opt-in failed.");
    }
    internal void ListClearSyntheticSmoke()
    {
        Refresh();
        if (list.Items.Count != 2) throw new InvalidOperationException("History list did not reload.");
        list.SelectedIndex = 0; ShowSelection();
        if (detail.Text.Contains("397,000") || detail.Text.Contains("DAX：")) throw new InvalidOperationException("History restored query rows.");
        RemoveSelected(); if (list.Items.Count != 1) throw new InvalidOperationException("History entry removal failed.");
        Mutate(() => store.Clear()); if (list.Items.Count != 0) throw new InvalidOperationException("History clear failed.");
        summaries.IsChecked = artifacts.IsChecked = false; SavePolicy();
        if (store.Load().Snapshot.Policy.SaveSummaries || store.Load().Snapshot.Policy.SaveArtifacts) throw new InvalidOperationException("History opt-out failed.");
    }
}
