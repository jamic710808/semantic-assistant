using System.Windows;
using System.Windows.Media;

namespace SemanticAssistant.Desktop;

public partial class PermissionsWindow : Window
{
    private readonly Action<bool> save;
    public PermissionsWindow(Window owner, bool allowed, Action<bool> save)
    {
        this.save = save; InitializeComponent();
        if (owner.IsLoaded) Owner = owner;
        AllowModification.IsChecked = allowed;
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try { save(AllowModification.IsChecked == true); DialogResult = true; }
        catch { HintText.Text = "保存失敗，原設定仍保留。請檢查本機設定檔是否可寫入。"; HintText.Foreground = Brushes.Firebrick; }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
