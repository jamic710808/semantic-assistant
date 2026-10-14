using System.Windows;
using System.Windows.Media;
using SemanticAssistant.Application;

namespace SemanticAssistant.Desktop;

public partial class QuickQuestionsWindow : Window
{
    private QuickQuestionSettings draft;
    private readonly Action<QuickQuestionSettings> save;
    private bool cloud;
    public QuickQuestionsWindow(Window owner, QuickQuestionSettings settings, Action<QuickQuestionSettings> save, bool initialCloud = false)
    {
        settings.Validate(); draft = settings.Normalize(); this.save = save;
        InitializeComponent(); if (owner.IsLoaded) Owner = owner;
        cloud = initialCloud; LoadInputs();
    }
    private void SaveInputs()
    {
        var questions = new QuickQuestionSet(Question1.Text.Trim(), Question2.Text.Trim(), Question3.Text.Trim());
        draft = (cloud ? draft with { Cloud = questions } : draft with { Local = questions }).Normalize();
    }
    private void LoadInputs()
    {
        var selected = cloud ? draft.Cloud : draft.Local;
        Question1.Text = selected.Question1; Question2.Text = selected.Question2; Question3.Text = selected.Question3;
        LocalButton.Background = cloud ? new SolidColorBrush(Color.FromRgb(245, 246, 250)) : new SolidColorBrush(Color.FromRgb(0, 126, 216));
        LocalButton.Foreground = cloud ? Brushes.DimGray : Brushes.White;
        CloudButton.Background = cloud ? new SolidColorBrush(Color.FromRgb(0, 126, 216)) : new SolidColorBrush(Color.FromRgb(245, 246, 250));
        CloudButton.Foreground = cloud ? Brushes.White : Brushes.DimGray;
        LocalButton.BorderThickness = new Thickness(cloud ? 1 : 0);
        CloudButton.BorderThickness = new Thickness(cloud ? 0 : 1);
        ModeHint.Text = cloud ? "設定雲端模型的三個快捷提問按鈕內容：" : "設定本地模型的三個快捷提問按鈕內容：";
    }
    private void OnMode(object sender, RoutedEventArgs e)
    {
        SaveInputs(); cloud = sender is FrameworkElement { Tag: "cloud" }; LoadInputs();
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try { SaveInputs(); draft.Validate(); save(draft); DialogResult = true; }
        catch { StatusText.Text = "保存失敗，原設定保留。請使用單行文字並確認本機設定檔可寫入。"; }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
