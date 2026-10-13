using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Contracts;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

internal static class AiDialogs
{
    public static CloudLoginOptions? CloudConfiguration(Window owner)
    {
        var window = new CloudLoginWindow(owner);
        return window.ShowDialog() == true ? window.Selection : null;
    }

    public static (ProviderIdentity Identity, bool Remember)? Configure(Window owner, ProviderIdentity? prior,
        WindowsCredentialVault vault, bool remember)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = Window(owner, "AI 供應商設定", panel);
        AddText(panel, "填入完整 Chat Completions 端點與模型名稱。設定本身不會連線。API key 儲存於 Windows 認證管理員。空白表示沿用此設定既有 key，或使用不需 key 的本機服務。");
        var endpoint = Input(panel, "端點 URL", prior?.Endpoint ?? "https://api.openai.com/v1/chat/completions");
        var model = Input(panel, "模型名稱（依供應商提供）", prior?.Model ?? "");
        AddText(panel, "API key");
        var password = new PasswordBox { Margin = new Thickness(0, 6, 0, 12), MaxLength = 2560 };
        panel.Children.Add(password);
        var retain = Check(panel, "記住這個端點與模型；重新啟動後仍需逐回合同意外送");
        retain.IsChecked = remember;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        ProviderIdentity? selected = null;
        var save = new Button { Content = "套用設定", Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try
            {
                selected = OpenAiCompatibleProvider.ValidateIdentity(new("openai-compatible", endpoint.Text.Trim(), model.Text.Trim()));
                if (password.Password.Length > 0) vault.SaveSecret(selected, password.Password);
                password.Clear();
                window.DialogResult = true;
            }
            catch { selected = null; status.Text = "設定未儲存。請確認 HTTPS 完整端點、模型名稱與 Windows 認證管理員可用。"; }
        };
        return window.ShowDialog() == true && selected is not null ? (selected, retain.IsChecked == true) : null;
    }

    public static AssistantBudgets? ConfigureBudgets(Window owner, AssistantBudgets prior)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = Window(owner, "本機執行預算", panel);
        AddText(panel, "預算保存於本機，只能在目前上限內調低。套用會清除舊回合與授權，沒有連線或執行查詢。結果暫存期限維持5分鐘。");
        var labels = new[] { "摘要表數（1–20）", "摘要字元（128–2000）", "查詢列數（1–100）", "查詢UTF-8大小（1024–64000 bytes）",
            "查詢秒數（1–30；本機最多10秒）", "AI回合數（1–6）", "工具呼叫數（1–8）", "AI輸出字元（128–20000）", "整輪秒數（1–120）" };
        var values = new[] { prior.ContextTables, prior.ContextCharacters, prior.QueryRows, prior.QueryBytes, prior.QueryTimeoutSeconds,
            prior.TurnRounds, prior.TurnToolCalls, prior.TurnCharacters, prior.TurnTimeoutSeconds };
        var inputs = labels.Select((label, i) => Input(panel, label, values[i].ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        AssistantBudgets? chosen = null;
        var apply = new Button { Content = "套用並保存預算" }; panel.Children.Add(apply);
        apply.Click += (_, _) =>
        {
            try
            {
                var parsed = inputs.Select(box => int.Parse(box.Text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                chosen = new(parsed[0], parsed[1], parsed[2], parsed[3], parsed[4], parsed[5], parsed[6], parsed[7], parsed[8]);
                chosen.Validate(); window.DialogResult = true;
            }
            catch { chosen = null; status.Text = "請依各欄上限填入整數。"; }
        };
        return window.ShowDialog() == true ? chosen : null;
    }

    public static EgressConsent? Consent(Window owner, ProviderIdentity identity, ModelIdentity target, bool canQuery, bool hasExternalTools = false)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = Window(owner, "當次回合的資料外送授權", panel);
        AddText(panel, $"接收端：{identity.Endpoint}\nAI 模型：{identity.Model}\n資料來源：{target.Name}\n來源版本：{target.Revision}\n\n授權只綁定此回合、此來源與此版本，最多 10 分鐘。工具結果可能包含商業資料；取消回合會撤銷後續傳送，已傳送的內容無法收回。");
        var message = Check(panel, "允許傳送我輸入的訊息（包括其中的模型名稱與數值）");
        var metadata = Check(panel, "允許傳送模型結構、欄位名稱與度量值 DAX");
        var query = Check(panel, "允許傳送 DAX 查詢與查詢結果資料列");
        query.IsEnabled = canQuery;
        var external = Check(panel, "允許傳送已匯入外部工具的名稱、描述、Schema、呼叫參數與結果（可能含商業資料；不含程序環境設定）");
        external.IsEnabled = hasExternalTools;
        var approve = new Button { Content = "授權並送出這一回合", Margin = new Thickness(0, 16, 0, 0), IsEnabled = false };
        panel.Children.Add(approve);
        message.Checked += (_, _) => approve.IsEnabled = true;
        message.Unchecked += (_, _) => approve.IsEnabled = false;
        approve.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return null;
        var categories = ImmutableHashSet.Create(EgressCategory.UserMessage);
        if (metadata.IsChecked == true) categories = categories.Add(EgressCategory.Metadata);
        if (canQuery && query.IsChecked == true) categories = categories.Add(EgressCategory.QueryResults);
        if (hasExternalTools && external.IsChecked == true) categories = categories.Add(EgressCategory.ExternalToolData);
        return new(identity, categories, DateTimeOffset.UtcNow.AddMinutes(10));
    }

    public static Task<bool> ConfirmExternalAsync(Window owner, ExternalToolPreview preview, CancellationToken token) =>
        owner.Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var panel = new StackPanel { Margin = new Thickness(20) };
            var window = Window(owner, "AI提出的外部工具操作 · 逐次同意", panel);
            AddText(panel, $"目的地：{preview.Source.Display}\n工具：{preview.OriginalName}\n清單版本：{preview.Source.Version}\n定義雜湊：{preview.Source.DefinitionHash}\n\n外部程序可能修改檔案、存取網路或產生未知副作用，不提供程序沙箱。以下是這一次實際交付的參數；未同意不執行，取消後不自動重試。");
            panel.Children.Add(new TextBox
            {
                Text = preview.Arguments.GetRawText(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 90,
                MaxHeight = 280,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
            var consent = Check(panel, "我同意將以上參數交給此程序，執行這一次操作");
            var apply = new Button { Content = "執行這一次操作", IsEnabled = false };
            panel.Children.Add(apply); consent.Checked += (_, _) => apply.IsEnabled = true; consent.Unchecked += (_, _) => apply.IsEnabled = false;
            apply.Click += (_, _) => window.DialogResult = true;
            using var cancellation = token.Register(() => owner.Dispatcher.BeginInvoke(() => { if (window.IsVisible) window.Close(); }));
            return window.ShowDialog() == true && !token.IsCancellationRequested;
        }).Task;

    private static Window Window(Window owner, string title, StackPanel panel) => new()
    {
        Owner = owner,
        Title = title,
        Width = 640,
        SizeToContent = SizeToContent.Height,
        MaxHeight = 750,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new ScrollViewer { Content = panel }
    };
    private static void AddText(StackPanel panel, string text) => panel.Children.Add(new TextBlock
    { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
    private static TextBox Input(StackPanel panel, string label, string value)
    {
        AddText(panel, label);
        var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 12), MaxLength = 2048 };
        panel.Children.Add(box); return box;
    }
    private static CheckBox Check(StackPanel panel, string text)
    {
        var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(box); return box;
    }
}
