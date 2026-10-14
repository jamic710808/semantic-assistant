using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

public partial class TemplatesWindow : Window
{
    private TemplatePreferences draft;
    private readonly Action<TemplatePreferences> save;
    private readonly string previewFolder;
    private ReportOutputKind output;
    private bool loading = true;

    public TemplatesWindow(Window owner, TemplatePreferences preferences, Action<TemplatePreferences> save, string storageRoot,
        ReportOutputKind initialOutput = ReportOutputKind.ReportMeasure)
    {
        preferences.Validate(); draft = preferences; this.save = save;
        previewFolder = Path.Combine(storageRoot, "template-previews");
        InitializeComponent(); if (owner.IsLoaded) Owner = owner;
        ShowPage(initialOutput);
    }
    private static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private void ShowPage(ReportOutputKind selected)
    {
        loading = true; output = selected;
        foreach (var tab in new[] { MeasureTab, FileTab, DashboardTab })
        {
            var active = int.Parse((string)tab.Tag) == (int)output;
            tab.Background = Color(active ? "#E6F2FF" : "#F8F8FD");
            tab.Foreground = Color(active ? "#007ED8" : "#5D718D");
        }
        PageTitle.Text = output switch { ReportOutputKind.ReportMeasure => "報告度量值範本", ReportOutputKind.HtmlFile => "獨立 HTML 範本", _ => "Dashboard 範本" };
        PageHint.Text = output switch
        {
            ReportOutputKind.ReportMeasure => "用於查詢結果轉 HTML 度量值；目前保存靜態快照。文字報告轉換將於後續加入。",
            ReportOutputKind.HtmlFile => "用於獨立 HTML 報告檔案，包含章節導覽與查詢來源。",
            _ => "用於 Dashboard 匯出、靜態及動態 HTML 度量值；動態內容隨篩選計算。"
        };
        var appearance = draft.For(output);
        TemplatePicker.ItemsSource = ReportTemplates.For(output);
        TemplatePicker.SelectedItem = ReportTemplates.For(output).First(t => t.Id == appearance.TemplateId);
        loading = false; RefreshStyle();
    }
    private void OnTab(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { Tag: string index }) ShowPage((ReportOutputKind)int.Parse(index)); }
    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || TemplatePicker.SelectedItem is not ReportTemplate template) return;
        draft = draft.With(output, new(template.Id, draft.For(output).PaletteId)); RefreshStyle();
    }
    private void RefreshStyle()
    {
        var style = ReportTemplates.Resolve(draft.For(output));
        DescriptionText.Text = style.Template.Description;
        PalettePanel.Children.Clear();
        foreach (var palette in ReportTemplates.Colors(output))
        {
            var chosen = palette.Id == style.Palette.Id;
            var label = new TextBlock { Text = (chosen ? "✓ " : "● ") + palette.Name, Foreground = Color(palette.Accent), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            gradient.GradientStops.Add(new((System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Background), 0));
            gradient.GradientStops.Add(new((System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Background), 0.55));
            gradient.GradientStops.Add(new((System.Windows.Media.Color)ColorConverter.ConvertFromString(palette.Accent), 1));
            var surface = new Border { CornerRadius = new CornerRadius(10), BorderBrush = Color(chosen ? "#438AFF" : palette.Border), BorderThickness = new Thickness(chosen ? 2 : 1), Padding = new Thickness(11),
                Background = gradient, Child = label };
            var button = new Button { Style = (Style)FindResource("Swatch"), Content = surface, Width = 145, Height = 45, Margin = new Thickness(0, 0, 12, 7), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            button.ToolTip = palette.Name;
            button.Click += (_, _) => { draft = draft.With(output, new(draft.For(output).TemplateId, palette.Id)); RefreshStyle(); };
            PalettePanel.Children.Add(button);
        }
        RenderPreview(style);
        StatusText.Text = "按保存套用三頁設定；取消會捨棄本次修改。";
    }
    private void RenderPreview(ResolvedReportAppearance style)
    {
        var p = style.Palette;
        PreviewSurface.Background = Color(p.Background); PreviewTitle.Foreground = Color(p.Accent); PreviewTitle.Text = style.Template.Name;
        PreviewLayout.Children.Clear(); PreviewLayout.RowDefinitions.Clear(); PreviewLayout.ColumnDefinitions.Clear();
        var overview = style.Template.Layout == ReportLayout.Overview;
        PreviewLayout.ColumnDefinitions.Add(new()); if (overview) PreviewLayout.ColumnDefinitions.Add(new());
        for (var i = 0; i < (overview ? 2 : 3); i++) PreviewLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var cards = new[] { ("回傳月份銷售合計", "397,000", false), ("月份銷售分布", "▅  ▇  ▆", false), ("月份與銷售明細", "2026-01  120,000    ·    2026-02  145,000    ·    2026-03  132,000", true) };
        if (!overview) cards = [cards[2], cards[0], cards[1]];
        for (var i = 0; i < cards.Length; i++)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = cards[i].Item1, FontSize = 11, Foreground = Color(p.Text) });
            content.Children.Add(new TextBlock { Text = cards[i].Item2, FontSize = cards[i].Item3 ? 10 : 19, Foreground = Color(p.Accent), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
            var card = new Border { Background = Color(p.Card), BorderBrush = Color(p.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(3), Child = content };
            Grid.SetRow(card, overview ? i == 2 ? 1 : 0 : i); Grid.SetColumn(card, overview && i == 1 ? 1 : 0);
            if (overview && i == 2) Grid.SetColumnSpan(card, 2);
            PreviewLayout.Children.Add(card);
        }
    }
    private void OnPreview(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(previewFolder);
            var path = Path.Combine(previewFolder, "synthetic-template-" + Guid.NewGuid().ToString("N") + ".html");
            File.WriteAllText(path, TemplatePreview.Html(draft.For(output)));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { StatusText.Text = "無法建立或開啟合成 HTML 預覽；設定尚未保存。"; }
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try { draft.Validate(); save(draft); DialogResult = true; }
        catch { StatusText.Text = "保存失敗；原設定保留，請檢查本機設定檔是否可寫入。"; }
    }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }
}
