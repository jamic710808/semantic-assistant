using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SemanticAssistant.Adapters.PowerBI.Local;

namespace SemanticAssistant.Desktop;

public sealed class DesktopReportPicker : Window
{
    private readonly ListBox reports;
    private readonly TextBlock status;
    private readonly Button connect;
    private readonly Button refresh;
    private readonly Func<Task<ImmutableArray<DesktopReportCandidate>>> reload;
    public DesktopReportCandidate? Selection { get; private set; }

    public DesktopReportPicker(Window owner, ImmutableArray<DesktopReportCandidate> candidates,
        Func<Task<ImmutableArray<DesktopReportCandidate>>> reload)
    {
        this.reload = reload;
        if (owner.IsLoaded) Owner = owner;
        Title = "選擇 Power BI 報告";
        Width = 500; Height = 350;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontFamily = owner.FontFamily;
        FontSize = 13;
        ShowInTaskbar = false;
        var panel = new Grid { Margin = new Thickness(22) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = "選擇 Power BI 報告", FontSize = 18, FontWeight = FontWeights.SemiBold });
        status = new TextBlock { Text = "偵測到正在執行的 Power BI 報告，請選擇連接：", Foreground = Brushes.Gray, Margin = new Thickness(0, 7, 0, 16) };
        Grid.SetRow(status, 1); panel.Children.Add(status);
        reports = new ListBox { Background = new SolidColorBrush(Color.FromRgb(249, 249, 250)), BorderThickness = new Thickness(0), Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        reports.ItemContainerStyle = ReportRowStyle();
        var border = new Border { CornerRadius = new CornerRadius(9), BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 229)), BorderThickness = new Thickness(1), Child = reports, Padding = new Thickness(1) };
        Grid.SetRow(border, 2); panel.Children.Add(border);
        var actions = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        refresh = ActionButton("重新偵測", false);
        refresh.Click += async (_, _) => await RefreshAsync();
        DockPanel.SetDock(refresh, Dock.Left); actions.Children.Add(refresh);
        connect = ActionButton("連接", true);
        connect.Click += (_, _) =>
        {
            if (reports.SelectedItem is not ListBoxItem { Tag: DesktopReportCandidate candidate }) return;
            Selection = candidate;
            DialogResult = true;
        };
        DockPanel.SetDock(connect, Dock.Right); actions.Children.Add(connect);
        var cancel = ActionButton("取消", false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
        Grid.SetRow(actions, 3); panel.Children.Add(actions);
        reports.SelectionChanged += (_, _) => connect.IsEnabled = reports.SelectedItem is ListBoxItem;
        Content = panel;
        SetReports(candidates);
    }

    private static Style ReportRowStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var surface = new FrameworkElementFactory(typeof(Border), "RowSurface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        surface.AppendChild(content);
        template.VisualTree = surface;
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(229, 242, 250)), "RowSurface"));
        template.Triggers.Add(selected);
        var focused = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.SteelBlue, "RowSurface"));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "RowSurface"));
        template.Triggers.Add(focused);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Button ActionButton(string label, bool primary)
    {
        var button = new Button { Content = label, MinWidth = 78, Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? new SolidColorBrush(Color.FromRgb(0, 126, 216)) : Brushes.White,
            Foreground = primary ? Brushes.White : Brushes.DimGray,
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 215, 220)), BorderThickness = new Thickness(primary ? 0 : 1) };
        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        surface.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        surface.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        surface.AppendChild(content); template.VisualTree = surface;
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.4)); template.Triggers.Add(disabled);
        button.Template = template;
        return button;
    }

    private void SetReports(ImmutableArray<DesktopReportCandidate> candidates)
    {
        reports.Items.Clear();
        foreach (var candidate in candidates)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 8) };
            row.Children.Add(new TextBlock { Text = "▥", Foreground = new SolidColorBrush(Color.FromRgb(233, 176, 0)), FontSize = 19, Margin = new Thickness(0, 0, 12, 0) });
            row.Children.Add(new TextBlock { Text = candidate.DisplayName, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 305, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            row.Children.Add(new TextBlock { Text = $"  (Port: {candidate.Instance.Owner.Port})", Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            reports.Items.Add(new ListBoxItem { Content = row, Tag = candidate, ToolTip = candidate.DisplayName, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        reports.SelectedIndex = candidates.IsEmpty ? -1 : 0;
        connect.IsEnabled = !candidates.IsEmpty;
        status.Text = candidates.IsEmpty ? "目前沒有可用的本機模型。開啟 Power BI 報告後，按「重新偵測」。" : "偵測到正在執行的 Power BI 報告，請選擇連接：";
    }

    private async Task RefreshAsync()
    {
        refresh.IsEnabled = connect.IsEnabled = false;
        status.Text = "正在重新偵測…";
        try { SetReports(await reload()); }
        catch { reports.Items.Clear(); status.Text = "偵測未完成，請確認 Power BI 仍在執行後重試。"; }
        finally { refresh.IsEnabled = true; connect.IsEnabled = reports.SelectedItem is ListBoxItem; }
    }
}
