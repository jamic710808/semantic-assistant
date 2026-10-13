using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SemanticAssistant.Adapters.Mcp;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Desktop;

internal sealed class McpToolsWindow : Window
{
    private readonly ModelIdentity target;
    private readonly Action<McpAiToolBinding>? importSelected;
    private readonly Button import = new() { Content = "匯入所選工具供本次AI使用" };
    private McpStdioClient? client;
    private CancellationTokenSource? running;
    private readonly TextBox executable = new() { MaxLength = 2048 };
    private readonly TextBox arguments = new() { Text = "[]", MaxLength = 16000 };
    private readonly TextBox workingDirectory = new() { MaxLength = 2048 };
    private readonly PasswordBox environment = new() { MaxLength = 16000 };
    private readonly ListBox tools = new() { DisplayMemberPath = "Name", MinHeight = 80 };
    private readonly TextBox details = ReadonlyBox();
    private readonly TextBox input = new() { Text = "{}", AcceptsReturn = true, Height = 80, MaxLength = 16000 };
    private readonly TextBox preview = ReadonlyBox();
    private readonly TextBox output = ReadonlyBox();
    private readonly CheckBox approve = new() { Content = "我同意將上述參數交給這個程序，執行這一次工具", IsEnabled = false };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button start = new() { Content = "啟動並列出工具" }, refresh = new() { Content = "重新列出工具" },
        prepare = new() { Content = "預覽呼叫" }, execute = new() { Content = "執行已同意的呼叫" }, cancel = new() { Content = "取消" };
    private (McpToolDescriptor Tool, JsonElement Arguments)? draft;
    private bool allowClose;
    private Task? shutdown;

    public McpToolsWindow(Window owner, ModelIdentity target, Action<McpAiToolBinding>? importSelected = null)
    {
        this.importSelected = importSelected;
        this.target = target; Owner = owner; Title = "MCP 外部工具 · 逐次同意"; Width = 920; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Label(panel, "外部程序依目前使用者權限執行，不提供程序沙箱。工具描述／readOnly annotation 是程序提供的資訊，沒有自動授權效力。工具結果不會自動送 AI。設定與環境值不保存到檔案。");
        Label(panel, "可執行檔完整路徑（以 interpreter 啟動腳本時，腳本放在參數中）"); panel.Children.Add(executable);
        var browse = new Button { Content = "選取可執行檔", Margin = new Thickness(0, 6, 0, 6) }; panel.Children.Add(browse);
        browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Executable|*.exe|All files|*.*" }; if (picker.ShowDialog(this) == true) executable.Text = picker.FileName; };
        Label(panel, "參數 JSON 字串陣列（逐項传入，不使用 shell 拼接）"); panel.Children.Add(arguments);
        Label(panel, "工作目錄完整路徑（留白使用可執行檔所在目錄）"); panel.Children.Add(workingDirectory);
        Label(panel, "環境變數 JSON（隱藏輸入；留白為空；不繼承既有 API key／token）"); panel.Children.Add(environment);
        Buttons(panel, start, refresh, cancel);
        Label(panel, "工具清單（全部要求 ExternalTool；只有明確匯入才可供AI選用）"); panel.Children.Add(tools); panel.Children.Add(details); Buttons(panel, import);
        Label(panel, "呼叫參數 JSON"); panel.Children.Add(input); Buttons(panel, prepare); panel.Children.Add(preview);
        panel.Children.Add(approve); Buttons(panel, execute); panel.Children.Add(status); panel.Children.Add(output);
        tools.SelectionChanged += (_, _) =>
        {
            ClearDraft(); if (tools.SelectedItem is McpToolDescriptor tool)
                details.Text = $"來源：{tool.Source}\n能力：{tool.RequiredCapability}（外部程序可能有未知副作用）\n版本：{tool.CatalogVersion}/{tool.DefinitionHash}\n\n{tool.Description}\n\n{tool.InputSchema.GetRawText()}";
        };
        import.Click += async (_, _) => await ImportSelectedAsync(false);
        input.TextChanged += (_, _) => ClearDraft();
        start.Click += async (_, _) => await StartAsync(); refresh.Click += async (_, _) => await RefreshAsync();
        prepare.Click += async (_, _) => await PrepareAsync(); execute.Click += async (_, _) => await ExecuteAsync(); cancel.Click += (_, _) => running?.Cancel();
        Closing += async (_, e) => { if (allowClose) return; e.Cancel = true; await ShutdownAsync(); };
        SetBusy(false);
    }

    private async Task StartAsync()
    {
        if (running is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25)); running = cancellation; SetBusy(true); ClearDraft();
        try
        {
            var args = StrictJson.Parse<ImmutableArray<string>>(arguments.Text, 16000, 8);
            var env = string.IsNullOrWhiteSpace(environment.Password) ? ImmutableDictionary<string, string>.Empty :
                StrictJson.Parse<ImmutableDictionary<string, string>>(environment.Password, 16000, 8);
            var config = new McpProcessConfiguration(executable.Text, args, env, string.IsNullOrWhiteSpace(workingDirectory.Text) ? null : workingDirectory.Text);
            config.Validate();
            if (client is not null) await client.DisposeAsync();
            client = await McpStdioClient.StartAsync(config, cancellation.Token);
            environment.Clear();
            tools.ItemsSource = await client.ListToolsAsync(cancellation.Token);
            status.Text = "程序已啟動；工具尚未獲准執行。請選取工具與預覽參數。";
        }
        catch { status.Text = "程序啟動／工具列舉失敗或已取消。請檢查路徑、JSON 與支援的 stdio 協定。"; }
        finally { running = null; SetBusy(false); }
    }
    private async Task RefreshAsync()
    {
        if (running is not null || client is null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25)); running = cancellation; SetBusy(true); ClearDraft();
        try { tools.ItemsSource = await client.ListToolsAsync(cancellation.Token); status.Text = "工具清單已更新，先前預覽／同意失效。"; }
        catch { status.Text = "列舉失敗或程序已結束；請重新啟動。"; }
        finally { running = null; SetBusy(false); }
    }
    private async Task PrepareAsync()
    {
        if (running is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8)); running = cancellation; SetBusy(true);
        ClearDraft();
        try
        {
            if (tools.SelectedItem is not McpToolDescriptor tool || client is null) return;
            using var doc = JsonDocument.Parse(input.Text, new() { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var chosen = doc.RootElement.Clone();
            await client.ValidateForPreviewAsync(tool, chosen, cancellation.Token);
            draft = (tool, chosen);
            preview.Text = $"當次目的地：{tool.Source}\n工具：{tool.Name}\n能力：{tool.RequiredCapability}\n\n將傳送的參數：\n{draft.Value.Arguments.GetRawText()}";
            approve.IsEnabled = true;
        }
        catch { ClearDraft(); status.Text = "參數或Schema未通過驗證、來源已改變或驗證已取消／逾時；未取得當次同意，沒有呼叫工具。"; }
        finally { running = null; SetBusy(false); }
    }
    private async Task ExecuteAsync()
    {
        if (running is not null || client is null || draft is not { } chosen || approve.IsChecked != true)
        { status.Text = "尚未完成當次預覽與同意；沒有執行工具。"; return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25)); running = cancellation; SetBusy(true);
        var session = new PermissionSession(target, ConversationMode.Analyze, ImmutableHashSet.Create(Capability.ExternalTool));
        try
        {
            var permissions = session.Snapshot;
            var registry = new ToolRegistry(ToolDispatcher.Definitions);
            var selected = new McpAiToolBinding(client, chosen.Tool, target);
            registry.ImportAfterUserSelection(selected);
            var policy = new ToolPolicyEngine(registry, session);
            policy.Check(selected.Descriptor, permissions);
            var approval = await client.ApproveAfterUserConfirmationAsync(chosen.Tool, chosen.Arguments, session, permissions, cancellation.Token);
            policy.Check(selected.Descriptor, permissions);
            var result = await client.CallToolAsync(chosen.Tool, chosen.Arguments, approval, session, permissions, cancellation.Token);
            policy.Check(selected.Descriptor, permissions);
            output.Text = string.Join("\n", result.Content.EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(x => x.GetProperty("text").GetString()));
            status.Text = result.IsError ? "工具回報失敗；結果只顯示為文字。" : "工具已執行；結果未自動送 AI，也沒有跟隨資源連結。";
        }
        catch { output.Clear(); status.Text = "工具呼叫失敗、取消或授權失效。外部操作可能已執行；沒有自動重試。"; }
        finally { session.Update(target, session.Snapshot.Mode, ImmutableHashSet<Capability>.Empty); running = null; ClearDraft(false); SetBusy(false); }
    }
    private void ClearDraft(bool clearResult = true)
    { draft = null; approve.IsChecked = false; approve.IsEnabled = false; preview.Clear(); if (clearResult) output.Clear(); }
    private async Task ImportSelectedAsync(bool syntheticConfirmed)
    {
        if (running is not null || client is null || importSelected is null || tools.SelectedItem is not McpToolDescriptor tool) return;
        if (!syntheticConfirmed && MessageBox.Show(this,
            $"匯入工具：{tool.Name}\n來源：{tool.Source}\n\n工具描述與Schema將在另行外送同意後提供給AI。每次執行仍需預覽參數並同意。程序會保留至來源／模式變更、清除匯入或主視窗關閉。",
            "明確選取外部工具", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            var binding = new McpAiToolBinding(client, tool, target);
            importSelected(binding); client = null; // Ownership is transferred only after successful registration.
            await ShutdownAsync();
        }
        catch { status.Text = "匯入未成功；來源或清單已變更、已有同名工具或超出八工具上限。"; }
    }
    internal async Task RunSyntheticImportSmokeAsync(string fixtureDll)
    {
        executable.Text = System.Environment.ProcessPath!; arguments.Text = JsonSerializer.Serialize(new[] { fixtureDll, "counting" });
        await StartAsync(); if (client is null || tools.Items.Count != 1) throw new InvalidOperationException("Import fixture failed.");
        tools.SelectedIndex = 0; await ImportSelectedAsync(true);
        if (client is not null) throw new InvalidOperationException("MCP client ownership was not transferred.");
    }
    internal Task ShutdownAsync() => shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        running?.Cancel();
        while (running is not null) await Task.Delay(50);
        if (client is not null) await client.DisposeAsync();
        allowClose = true; Close();
    }
    private void SetBusy(bool busy)
    {
        import.IsEnabled = !busy && importSelected is not null && client?.IsAlive == true;
        start.IsEnabled = !busy; refresh.IsEnabled = !busy && client?.IsAlive == true;
        prepare.IsEnabled = !busy && client?.IsAlive == true; execute.IsEnabled = !busy && client?.IsAlive == true;
        executable.IsEnabled = !busy; arguments.IsEnabled = !busy; environment.IsEnabled = !busy; workingDirectory.IsEnabled = !busy;
        input.IsEnabled = !busy; tools.IsEnabled = !busy; cancel.IsEnabled = busy;
    }
    internal async Task RunSyntheticSmokeAsync(string fixtureDll)
    {
        executable.Text = System.Environment.ProcessPath!;
        arguments.Text = JsonSerializer.Serialize(new[] { fixtureDll, "normal" });
        await StartAsync(); if (client is null || tools.Items.Count != 1) throw new InvalidOperationException("MCP smoke 初始化失敗。");
        tools.SelectedIndex = 0; input.Text = "{\"message\":4}"; await PrepareAsync();
        if (draft is not null || approve.IsEnabled || output.Text.Length != 0) throw new InvalidOperationException("Invalid schema argument reached consent.");
        tools.SelectedIndex = 0; input.Text = "{\"message\":\"桌面合成測試\"}"; await PrepareAsync();
        await ExecuteAsync(); if (output.Text.Length != 0) throw new InvalidOperationException("未同意卻執行。");
        approve.IsChecked = true; await ExecuteAsync();
        if (!output.Text.Contains("合成結果：桌面合成測試") || approve.IsChecked == true || draft is not null)
            throw new InvalidOperationException("MCP 桌面呼叫不符。");
        input.Text = "{\"message\":\"different\"}";
        if (output.Text.Length != 0) throw new InvalidOperationException("Input change retained previous tool result.");
    }
    private static TextBox ReadonlyBox() => new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 60,
        MaxHeight = 150,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 6, 0, 6)
    };
    private static void Label(Panel panel, string text) => panel.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 6)
    });
    private static void Buttons(Panel panel, params Button[] buttons)
    { var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) }; foreach (var button in buttons) row.Children.Add(button); panel.Children.Add(row); }
}
