using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Desktop;

public partial class AiProfilesWindow : Window
{
    private AssistantSettings state;
    private readonly WindowsCredentialVault vault;
    private readonly Action<AssistantSettings, bool> commit;
    private string? selectedId;
    private bool loading = true;
    private bool dirty;

    public AiProfilesWindow(Window owner, AssistantSettings settings, WindowsCredentialVault vault,
        Action<AssistantSettings, bool> commit)
    {
        state = settings.IncludeCurrentProfile();
        state.Validate();
        this.vault = vault;
        this.commit = commit;
        InitializeComponent();
        if (owner.IsLoaded) Owner = owner;
        Closing += OnClosing;
        RefreshList(state.AiProvider?.Id ?? state.AiProfiles.FirstOrDefault()?.Identity.Id);
    }

    private SavedAiProfile? Selected => state.AiProfiles.FirstOrDefault(p => p.Identity.Id == selectedId);

    private void RefreshList(string? id)
    {
        loading = true;
        ProfilesList.Items.Clear();
        foreach (var profile in state.AiProfiles)
        {
            var card = new StackPanel();
            var title = new DockPanel();
            if (profile.Identity == state.AiProvider)
            {
                var badge = new TextBlock { Text = "目前使用", Foreground = Brushes.DodgerBlue, FontSize = 10, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(badge, Dock.Right); title.Children.Add(badge);
            }
            title.Children.Add(new TextBlock { Text = profile.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            card.Children.Add(title);
            card.Children.Add(new TextBlock { Text = profile.Identity.Model, Foreground = Brushes.SlateGray, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 7, 0, 0) });
            ProfilesList.Items.Add(new ListBoxItem { Style = (Style)FindResource(typeof(ListBoxItem)), Tag = profile.Identity.Id, Content = card, ToolTip = profile.DisplayName + "\n" + profile.Identity.Endpoint });
        }
        selectedId = id;
        ProfilesList.SelectedItem = ProfilesList.Items.Cast<ListBoxItem>().FirstOrDefault(item => (string)item.Tag == id);
        ActiveSummary.Text = state.AiProvider is null ? "目前使用：本機模擬 AI" : "目前使用：" +
            (state.AiProfiles.FirstOrDefault(p => p.Identity == state.AiProvider)?.DisplayName ?? state.AiProvider.Model);
        LoadEditor();
        loading = false;
    }

    private void LoadEditor()
    {
        var priorLoading = loading;
        loading = true;
        var saved = Selected;
        DisplayNameInput.Text = saved?.DisplayName ?? "";
        EndpointInput.Text = saved?.Identity.Endpoint ?? "https://api.openai.com/v1/chat/completions";
        ModelInput.Text = saved?.Identity.Model ?? "";
        KeyInput.Clear(); RemoveKey.IsChecked = false;
        try { KeyStatus.Text = saved is not null && vault.HasSecret(saved.Identity) ? "已有保存的 key；留白即可沿用。" : "尚未保存 key；需要驗證的供應商請填入。"; }
        catch { KeyStatus.Text = "無法讀取憑證狀態；未顯示或修改既有 key。"; }
        EditorTitle.Text = saved is null ? "新增模型" : "編輯模型";
        ActiveBadge.Text = saved is not null && saved.Identity == state.AiProvider ? "目前使用" : "";
        dirty = false;
        loading = priorLoading;
        UpdateActions();
    }

    private void UpdateActions()
    {
        var saved = Selected;
        ActivateButton.IsEnabled = saved is not null && !dirty && saved.Identity != state.AiProvider;
        DeleteButton.IsEnabled = saved is not null;
        AddButton.IsEnabled = state.AiProfiles.Length < 24;
        SaveButton.Content = dirty ? "保存設定＊" : "保存設定";
    }

    private void OnEdited(object sender, TextChangedEventArgs e) => MarkEdited();
    private void OnPasswordEdited(object sender, RoutedEventArgs e) => MarkEdited();
    private void OnKeyOptionChanged(object sender, RoutedEventArgs e) => MarkEdited();
    private void MarkEdited()
    {
        if (loading) return;
        dirty = true;
        StatusText.Text = "尚未保存；保存後可設為目前使用。";
        UpdateActions();
    }

    private bool CanDiscard() => !dirty || MessageBox.Show(this,
        "這組模型有尚未保存的修改，是否捨棄？", "尚未保存", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (!CanDiscard())
        {
            loading = true;
            ProfilesList.SelectedItem = ProfilesList.Items.Cast<ListBoxItem>().FirstOrDefault(item => (string)item.Tag == selectedId);
            loading = false;
            return;
        }
        selectedId = (ProfilesList.SelectedItem as ListBoxItem)?.Tag as string;
        LoadEditor(); StatusText.Text = "";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!CanDiscard()) return;
        RefreshList(null); StatusText.Text = "填入新模型後按「保存設定」。";
        DisplayNameInput.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var prior = Selected;
        ProviderIdentity? identity = null;
        string? destinationSecret = null;
        var secretMutated = false;
        var committed = false;
        try
        {
            var name = DisplayNameInput.Text.Trim();
            identity = ProviderProfiles.Validate(new(prior?.Identity.Id ?? "ai-profile-" + Guid.NewGuid().ToString("N"),
                EndpointInput.Text.Trim(), ModelInput.Text.Trim()));
            var profile = new SavedAiProfile(name, identity);
            var list = prior is null ? state.AiProfiles.Add(profile)
                : state.AiProfiles.Select(p => p.Identity.Id == prior.Identity.Id ? profile : p).ToImmutableArray();
            var candidate = state with
            {
                AiProfiles = list,
                AiProvider = prior is not null && state.AiProvider == prior.Identity ? identity : state.AiProvider
            };
            candidate.Validate();
            if (RemoveKey.IsChecked == true && KeyInput.Password.Length != 0)
                throw new ArgumentException("請選擇填入新 key 或清除 key。");

            var changeKey = RemoveKey.IsChecked == true || KeyInput.Password.Length != 0 || prior is not null && prior.Identity != identity;
            if (changeKey)
            {
                destinationSecret = vault.ReadSecret(identity);
                var replacement = RemoveKey.IsChecked == true ? null
                    : KeyInput.Password.Length != 0 ? KeyInput.Password
                    : prior is not null ? vault.ReadSecret(prior.Identity) : null;
                if (replacement is null) vault.DeleteSecret(identity);
                else vault.SaveSecret(identity, replacement);
                secretMutated = true;
            }
            // The host persists first; UI and active provider change only on success.
            commit(candidate, changeKey && candidate.AiProvider == identity);
            committed = true;
            state = candidate;
            KeyInput.Clear();
            RefreshList(identity.Id);
            StatusText.Text = "設定已保存。" + (candidate.AiProvider == identity ? "目前使用這個模型。" : "可按「設為目前使用」啟用。" );
            if (prior is not null && prior.Identity != identity)
            {
                try { vault.DeleteSecret(prior.Identity); }
                catch { StatusText.Text += " 舊端點憑證清理未完成。"; }
            }
        }
        catch
        {
            var rollbackFailed = false;
            if (!committed && secretMutated && identity is not null)
            {
                try { if (destinationSecret is null) vault.DeleteSecret(identity); else vault.SaveSecret(identity, destinationSecret); }
                catch { rollbackFailed = true; }
            }
            KeyInput.Clear();
            StatusText.Text = rollbackFailed
                ? "設定未保存，憑證回復未完成；請重新設定此模型的 key。"
                : "設定未保存。請核對顯示名稱、完整 HTTPS 端點、模型名稱與憑證；清除 key 時請勿同時填入新 key。";
        }
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        if (dirty || Selected is not { } selected) return;
        try
        {
            var candidate = state with { AiProvider = selected.Identity };
            commit(candidate, false); state = candidate;
            RefreshList(selected.Identity.Id);
            StatusText.Text = "已設為目前使用；下次啟動會沿用，可直接送出提問。";
        }
        catch { StatusText.Text = "切換未保存；目前使用的模型保持原設定。"; }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        if (MessageBox.Show(this, $"刪除「{selected.DisplayName}」及其保存的 key？" +
            (state.AiProvider == selected.Identity ? "\n目前使用的模型將改回本機模擬 AI。" : ""),
            "刪除模型", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var candidate = state with { AiProfiles = state.AiProfiles.Where(p => p.Identity.Id != selected.Identity.Id).ToImmutableArray(),
                AiProvider = state.AiProvider == selected.Identity ? null : state.AiProvider };
            commit(candidate, false); state = candidate;
            RefreshList(state.AiProvider?.Id ?? state.AiProfiles.FirstOrDefault()?.Identity.Id);
            try { vault.DeleteSecret(selected.Identity); StatusText.Text = "模型與保存的 key 已刪除。"; }
            catch { StatusText.Text = "模型設定已刪除；Windows 憑證清理未完成。"; }
        }
        catch { StatusText.Text = "刪除未保存；模型設定仍保留。"; }
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!CanDiscard()) { e.Cancel = true; return; }
        loading = true; KeyInput.Clear();
    }
}
