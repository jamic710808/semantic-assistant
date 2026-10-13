using System.Windows;
using System.Windows.Controls;
using SemanticAssistant.Adapters.PowerBI.Cloud;

namespace SemanticAssistant.Desktop;

internal sealed record CloudLoginOptions(Guid ClientId, Guid TenantId, bool RememberLogin, bool SelectDifferentAccount);

internal sealed class CloudLoginWindow : Window
{
    public CloudLoginOptions? Selection { get; private set; }
    public CloudLoginWindow(Window owner, string? cacheDirectory = null)
    {
        NameScope.SetNameScope(this, new NameScope());
        Owner = owner; Title = "Power BI 雲端使用者登入"; Width = 660; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Text(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        Text("需要 Entra public-client app、http://localhost redirect 及 Dataset.Read.All／Workspace.Read.All 委派權限。模型結構使用 XMLA，需合適容量／PPU、XMLA read 與 Build 權限；REST 查詢需租用戶啟用 ExecuteQueries。");
        Text("預設開啟系統瀏覽器選帳號，登入只保留在記憶體。若勾選記住登入，下次填入相同兩個 ID 並再次勾選，可續用同一帳號；需要重新驗證時仍會開啟瀏覽器。");
        TextBox Input(string name, string label)
        {
            Text(label); var field = new TextBox { MaxLength = 36, Margin = new Thickness(0, 0, 0, 12) };
            RegisterName(name, field); panel.Children.Add(field); return field;
        }
        var client = Input("CloudClient", "Application (client) ID");
        var tenant = Input("CloudTenant", "Directory (tenant) ID");
        var remember = new CheckBox { Content = "記住登入30天（由目前 Windows 使用者加密保存）", IsChecked = false, Margin = new Thickness(0, 8, 0, 8) };
        var different = new CheckBox { Content = "改選其他帳號；成功後取代此應用程式／租用戶的已保存登入", IsChecked = false, Margin = new Thickness(0, 0, 0, 12) };
        RegisterName("RememberCloud", remember); RegisterName("DifferentCloudAccount", different);
        panel.Children.Add(remember); panel.Children.Add(different);
        Text("登入快取不包含查詢、模型修改或AI外送同意。取消勾選只停用本次快取，不刪除先前保存的副本；可使用下方清除按鈕。登出可清除本機副本，但不撤銷服務端既有授權。");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; RegisterName("CloudLoginStatus", status); panel.Children.Add(status);
        bool Ids(out Guid c, out Guid t)
        {
            c = Guid.Empty; t = Guid.Empty;
            if (Guid.TryParse(client.Text, out c) && c != Guid.Empty && Guid.TryParse(tenant.Text, out t) && t != Guid.Empty) return true;
            status.Text = "請填入兩個有效 GUID。"; return false;
        }
        var clear = new Button { Content = "清除此應用程式／租用戶的已保存登入", Margin = new Thickness(0, 12, 0, 8) };
        RegisterName("ClearCloudLogin", clear); panel.Children.Add(clear);
        clear.Click += (_, _) =>
        {
            if (!Ids(out var c, out var t)) return;
            try { CloudLoginCache.Forget(c, t, cacheDirectory); status.Text = "此組設定的本機登入快取已清除。"; }
            catch { status.Text = "快取無法清除；若目前正在使用此登入，請先關閉視窗並登出雲端，再重試。"; }
        };
        var login = new Button { Content = "登入並選取工作區", Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(login);
        login.Click += (_, _) =>
        {
            if (!Ids(out var c, out var t)) return;
            Selection = new(c, t, remember.IsChecked == true, different.IsChecked == true); DialogResult = true;
        };
    }
}
