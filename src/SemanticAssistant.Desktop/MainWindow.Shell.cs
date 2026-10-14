using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Text;
using System.IO;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

public partial class MainWindow
{
    private void ApplyDefaultPermissions()
    {
        AllowQuery.IsChecked = !workspace.Current.IsOffline;
        AllowExport.IsChecked = true;
        AllowExternalTools.IsChecked = modificationPolicy.Snapshot.Allowed;
    }

    private void ShowWelcome()
    {
        RefreshQuickQuestions();
        Transcript.ClearMessages();
        ConversationScroll.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
        WelcomeText.Text = workspace.Current.Source == ModelSourceKind.Synthetic
            ? "我是你的 Semantic 智能助手，已載入合成示範模型，你可以向我提出需求～"
            : aiProfile is null
                ? "已讀取模型結構。你可以選取左側物件；使用 AI 分析前，請先設定供應商。"
                : "我是你的 Semantic 智能助手，已讀取模型結構，你可以向我提出需求～";
        BackToChatButton.Visibility = Visibility.Collapsed;
    }

    private void ShowConversation()
    {
        WelcomePanel.Visibility = Visibility.Collapsed;
        ConversationScroll.Visibility = Visibility.Visible;
    }

    private void OnCopyAnswer(object sender, RoutedEventArgs e)
    {
        if (conversation.LatestReport is not { } report) { SourceStatus.Text = "目前沒有可複製的回答。"; return; }
        CopyConversationDocument(MarkdownFlowDocument.Answer(report.Markdown), "已複製最新回答與排版，可貼到 Word。", report.Markdown);
    }

    private void OnCopyConversation(object sender, RoutedEventArgs e) => CopyConversationDocument(Transcript.Document,
        "已複製全部提問、回答與排版。", Transcript.Text);

    private void CopyConversationDocument(FlowDocument document, string success, string markdown)
    {
        try
        {
            var range = new TextRange(document.ContentStart, document.ContentEnd);
            using var buffer = new MemoryStream(); range.Save(buffer, DataFormats.Rtf);
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, range.Text.Trim());
            data.SetData(DataFormats.Rtf, Encoding.ASCII.GetString(buffer.ToArray()));
            data.SetData("text/markdown", markdown);
            Clipboard.SetDataObject(data, true); SourceStatus.Text = success;
        }
        catch { CopyConversationText(markdown, "已複製原始回答文字；本次無法帶出排版。"); }
    }

    private void AppendExecutionLog(string message)
    {
        // Host events only, in memory. Do not include provider text, credentials or raw result payloads.
        const int maximum = 32_000;
        var remaining = maximum - ExecutionDetails.Text.Length;
        if (remaining <= 1) return;
        var line = (ExecutionDetails.Text.Length == 0 ? "" : "\n") + message;
        if (line.Length > remaining)
        {
            const string omitted = "\n（執行紀錄已達顯示上限）";
            var length = Math.Max(0, remaining - omitted.Length);
            if (length > 0 && char.IsHighSurrogate(line[length - 1])) length--;
            line = line[..length] + (omitted.Length <= remaining ? omitted : "");
        }
        ExecutionDetails.AppendText(line);
    }

    private void CopyConversationText(string? text, string success)
    {
        if (string.IsNullOrWhiteSpace(text)) { SourceStatus.Text = "目前沒有可複製的內容。"; return; }
        try { Clipboard.SetText(text); SourceStatus.Text = success; }
        catch { SourceStatus.Text = "剪貼簿目前無法寫入；請選取文字後按 Ctrl+C，或稍後重試。"; }
    }

    private void SyncShellState(bool busy)
    {
        ReportModeButton.IsChecked = ModePicker.SelectedIndex == 1;
        HtmlModeButton.IsChecked = ModePicker.SelectedIndex is 2 or 3;
        DashboardModeButton.IsChecked = ModePicker.SelectedIndex == 4;
        ReportModeButton.IsEnabled = HtmlModeButton.IsEnabled = DashboardModeButton.IsEnabled = !busy;
        AnalyzeModeButton.IsEnabled = !busy;
        AnalyzeModeButton.Foreground = ModePicker.SelectedIndex == 0
            ? new SolidColorBrush(Color.FromRgb(0, 108, 184)) : Brushes.SlateGray;
        TemplatesButton.IsEnabled = GuideButton.IsEnabled = !busy;
        QuickPrompts.IsEnabled = !busy;
        AttachmentButton.IsEnabled = NewConversationButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.ToolTip = SendButton.Content;
        if (ModePicker.SelectedIndex == 3 && !modificationPolicy.Snapshot.Allowed)
            SendButton.ToolTip = "安全模式禁止建立 HTML 度量值；請先到權限管理允許修改模型。";
        if (ModePicker.SelectedIndex != 3 && workspace.Current.Source != ModelSourceKind.Synthetic && aiProfile is null)
            SendButton.ToolTip = "可以先填寫提問；送出前請到「AI 配置」設定供應商。";
    }

    private void OnModeChip(object sender, RoutedEventArgs e)
    {
        if (running is not null || sender is not FrameworkElement { Tag: string index } || !int.TryParse(index, out var value)) return;
        ModePicker.SelectedIndex = value;
        SyncShellState(false);
    }

    private void OnAnalyzeMode(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        ModePicker.SelectedIndex = 0;
    }

    private void FillPrompt(string prompt)
    {
        if (running is not null) return;
        ContentTabs.SelectedIndex = 0;
        UtilityPanel.Visibility = Visibility.Collapsed;
        MessageInput.Text = prompt;
        MessageInput.Focus();
        MessageInput.CaretIndex = MessageInput.Text.Length;
        SourceStatus.Text = workspace.Current.Source != ModelSourceKind.Synthetic && aiProfile is null
            ? "已填入提問 · 送出前請先設定 AI 供應商"
            : "已填入提問 · 可以修改後按送出開始分析";
    }

    private void OnQuickPrompt(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string index } && int.TryParse(index, out var value) && value is >= 0 and < 3)
            FillPrompt(settings.QuickQuestions.For(workspace.Current.Source == ModelSourceKind.Cloud)[value]);
    }

    private void RefreshQuickQuestions()
    {
        var questions = settings.QuickQuestions.For(workspace.Current.Source == ModelSourceKind.Cloud);
        var buttons = new[] { QuickQuestionButton1, QuickQuestionButton2, QuickQuestionButton3 };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Content = new TextBlock { Text = questions[i], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            buttons[i].MaxWidth = 260;
            buttons[i].ToolTip = questions[i];
            System.Windows.Automation.AutomationProperties.SetName(buttons[i], questions[i]);
        }
    }

    private void OnMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (SendButton.IsEnabled) OnSend(SendButton, new RoutedEventArgs(Button.ClickEvent));
    }

    private void OnMessageChanged(object sender, TextChangedEventArgs e)
    {
        if (uiReady) SetBusy(running is not null);
    }

    private void OnShowTemplates(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        var selected = ModePicker.SelectedIndex switch { 4 => ReportOutputKind.Dashboard, 2 => ReportOutputKind.HtmlFile, _ => ReportOutputKind.ReportMeasure };
        try
        {
            new TemplatesWindow(this, settings.Templates, preferences =>
            {
                var candidate = settings with { Templates = preferences }; candidate.Validate();
                settingsStore.Save(candidate); settings = candidate;
                SourceStatus.Text = "範本已保存，後續輸出會套用；既有檔案與度量值保留。";
            }, storageRoot, selected).ShowDialog();
        }
        catch { SourceStatus.Text = "無法開啟範本設定；原設定仍保留。"; }
    }

    private void ShowUtility(int tab, string title)
    {
        UtilityTitle.Text = title;
        UtilityTabs.SelectedIndex = tab;
        UtilityPanel.Visibility = Visibility.Visible;
    }

    private void OnShowPermissions(object sender, RoutedEventArgs e)
    {
        try
        {
            UtilityPanel.Visibility = Visibility.Collapsed;
            new PermissionsWindow(this, settings.AllowModelModification, allowed =>
            {
                var candidate = settings with { AllowModelModification = allowed }; candidate.Validate();
                settingsStore.Save(candidate); settings = candidate;
                modificationPolicy.Update(allowed);
                if (!allowed && (session?.Snapshot.Allows(SemanticAssistant.Contracts.Capability.ExternalTool) == true ||
                    session?.Snapshot.Allows(SemanticAssistant.Contracts.Capability.DefinitionWrite) == true)) OnCancel(this, new());
                if (!allowed) { pendingAiWrite = null; pendingAiWriter = null; }
                if (!allowed) { AllowExternalTools.IsChecked = false; ClearImportedTools(); }
                SetBusy(running is not null);
                SourceStatus.Text = allowed ? "已保存：允許修改資料模型。" : "已保存：安全模式，禁止修改資料模型；查詢與 HTML 檔案匯出仍可使用。";
            }).ShowDialog();
        }
        catch { SourceStatus.Text = "無法開啟權限管理；原設定仍保留。"; }
    }
    private void OnShowTools(object sender, RoutedEventArgs e) => ShowUtility(1, "模型來源與工具");
    private void OnShowResults(object sender, RoutedEventArgs e) => ShowUtility(2, "查詢結果與匯出");
    private void OnCloseUtility(object sender, RoutedEventArgs e) => UtilityPanel.Visibility = Visibility.Collapsed;
    private void OnBackToChat(object sender, RoutedEventArgs e) => ContentTabs.SelectedIndex = 0;
    private void OnSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!uiReady || e.Source != ContentTabs) return;
        BackToChatButton.Visibility = ContentTabs.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNewConversation(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        InvalidateConversation();
        MessageInput.Clear();
        ContentTabs.SelectedIndex = 0;
        UtilityPanel.Visibility = Visibility.Collapsed;
        ShowWelcome();
        SetBusy(false);
        SourceStatus.Text = "已開始新對話 · 請重新設定當次需要的權限";
    }

    private void OnShowGuide(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        try
        {
            new QuickQuestionsWindow(this, settings.QuickQuestions, questions =>
            {
                var candidate = settings with { QuickQuestions = questions.Normalize() }; candidate.Validate();
                settingsStore.Save(candidate); settings = candidate;
                RefreshQuickQuestions();
                SourceStatus.Text = "提問配置已保存；首頁三個快捷按鈕已同步更新。";
            }).ShowDialog();
        }
        catch { SourceStatus.Text = "無法開啟提問配置，原設定仍保留。"; }
    }

    private void OnShowLanguage(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "目前介面與預設 AI 提問使用繁體中文。", "語言", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnShowAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow(this).ShowDialog();

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnWindowClose(object sender, RoutedEventArgs e) => Close();
}
