using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace SemanticAssistant.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow(Window owner)
    {
        InitializeComponent();
        if (owner.IsLoaded) Owner = owner;
        VersionText.Text = AppVersionInfo.VersionLabel;
        VersionText.ToolTip = $"完整版本：{AppVersionInfo.FullVersion}\n程式位置：{AppVersionInfo.ProgramPath}";
        BuildTimeText.Text = AppVersionInfo.BuildTimeLabel;
    }
    private void OnConfirm(object sender, RoutedEventArgs e) => Close();
    private void OnOpenRepository(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "無法開啟瀏覽器，請使用視窗中的原始碼網址。", "關於", MessageBoxButton.OK, MessageBoxImage.Information); }
        e.Handled = true;
    }
}
