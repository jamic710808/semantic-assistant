using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SemanticAssistant.Desktop;

public sealed record DisplayConversationMessage(bool IsAssistant, string Content);

// Holds presentation history only. AI context and report Markdown remain application-owned.
public sealed class MarkdownConversationView : RichTextBox
{
    private readonly List<DisplayConversationMessage> messages = [];
    private TextPointer? latestAnswerStart;
    public string Text => string.Join("\n\n", messages.Select(m => (m.IsAssistant ? "助手：\n" : "你：") + m.Content));
    public ImmutableArray<DisplayConversationMessage> Messages => messages.ToImmutableArray();

    public MarkdownConversationView()
    {
        IsReadOnly = true; IsReadOnlyCaretVisible = true;
        IsDocumentEnabled = true; BorderThickness = new Thickness(0); Padding = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = MarkdownFlowDocument.Empty();
    }

    public void ClearMessages()
    {
        messages.Clear(); latestAnswerStart = null; Document = MarkdownFlowDocument.Empty();
    }

    public void AppendUserMessage(string text) => Append(new(false, text));
    public void AppendAssistantMessage(string markdown) => Append(new(true, markdown));

    public void RestoreMessages(ImmutableArray<DisplayConversationMessage> prior)
    {
        ClearMessages(); foreach (var message in prior) Append(message);
    }

    private void Append(DisplayConversationMessage message)
    {
        var section = new Section { Margin = new Thickness(0, 0, 0, 24) };
        section.Blocks.Add(new Paragraph(new Run(message.IsAssistant ? "助手" : "你"))
        {
            FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8)
        });
        if (message.IsAssistant) MarkdownFlowDocument.Append(section.Blocks, message.Content);
        else section.Blocks.Add(new Paragraph(new Run(message.Content))
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
        });
        Document.Blocks.Add(section); messages.Add(message);
        if (message.IsAssistant) latestAnswerStart = section.ContentStart;
    }

    public void ShowLatestAnswer(ScrollViewer scroll)
    {
        // Navigate to the beginning of the new answer, rather than skipping its title.
        var document = Document; var start = latestAnswerStart;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (Document != document || start is null || !IsVisible) return;
            var rect = start.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return;
            var point = TranslatePoint(new Point(0, rect.Top), scroll);
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + point.Y));
        }));
    }
}
