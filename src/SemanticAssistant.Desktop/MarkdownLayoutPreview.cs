using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SemanticAssistant.Desktop;

// Export a layout sample using the production renderer, without a window, model connection or AI request.
internal static class MarkdownLayoutPreview
{
    private const string Sample = """
        這個 Power BI 資料模型由 **4 張表、2 個關係、5 個度量值**組成，基本結構如下。

        ## 1. 主要交易表：Sales

        `Sales` 是模型中的核心事實表，記錄銷售交易資料，主要欄位包括：

        - `Date`：交易日期
        - `Region key`：地區識別碼
        - `Revenue amount`：營收金額
        - `Cost amount`：成本金額
        - `Quantity`：銷售數量

        表中已有 5 個度量值：

        | 度量值 | 用途 |
        | --- | --- |
        | Revenue | 營收 |
        | Cost | 成本 |
        | Profit | 利潤 |
        | Margin | 利潤率 |
        | Units | 銷售數量 |

        ## 2. 日期維度表：Dates

        `Dates` 用於日期篩選與年度、月份分析，包含 `Date`、`Year`、`Month`、`Month number`。

        ## 3. 模型關係結構

        ```text
        Dates[Date] ─── Sales[Date]
        Regions[Region key] ─── Sales[Region key]
        ```

        `Review regions` 未連接其他表；**其業務用途仍需公式或報表使用證據確認**。
        """;

    public static void Write(string destination)
    {
        var path = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var view = new MarkdownConversationView(); view.AppendAssistantMessage(Sample);
        var frame = new Grid { Background = new SolidColorBrush(Color.FromRgb(247, 246, 252)) };
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition());
        var caption = new TextBlock
        {
            Text = "回答排版示例 · 使用提供的模型結構文字，未呼叫 AI",
            FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(44, 24, 44, 0)
        };
        frame.Children.Add(caption);
        var scroll = new ScrollViewer { Content = view, Margin = new Thickness(44, 24, 44, 24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); frame.Children.Add(scroll);
        var size = new Size(900, 1120); frame.Measure(size); frame.Arrange(new Rect(size)); frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 1120, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        // Also provide a editable rich-text sample for opening in Word.
        using var richText = File.Create(Path.ChangeExtension(path, ".rtf"));
        new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Save(richText, DataFormats.Rtf);
    }
}
