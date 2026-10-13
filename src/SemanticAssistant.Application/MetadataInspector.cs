using System.Collections.Immutable;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record MetadataNode(string Id, string Title, string Detail, ImmutableArray<MetadataNode> Children);

// All descriptions are locally constructed text, with no AI request or generated HTML.
public static class MetadataInspector
{
    private const int MaxDetailCharacters = 20_000;

    public static ImmutableArray<MetadataNode> Build(ModelSnapshot model, string search = "")
    {
        var tables = model.Tables.Select(table =>
        {
            var id = "table/" + Uri.EscapeDataString(table.Name);
            var columns = table.Columns.Select(column => new MetadataNode(id + "/column/" + Uri.EscapeDataString(column.Name),
                column.Name + " · " + column.DataType, Limit($"欄位：{column.Name}\n所屬表：{table.Name}\n資料型別：{column.DataType}\n" +
                    (column.Expression is null ? "" : $"\n計算欄位 DAX 定義（未執行）：\n{column.Expression}\n") + "\n這是結構資訊，沒有讀取資料列。"), [])).ToImmutableArray();
            var measures = table.Measures.Select(measure => new MetadataNode(id + "/measure/" + Uri.EscapeDataString(measure.Name),
                "∑ " + measure.Name, Limit($"度量值：{measure.Name}\n所屬表：{table.Name}\n\nDAX 定義（未執行）：\n{measure.Expression}"), [])).ToImmutableArray();
            return new MetadataNode(id, table.Name, $"表：{table.Name}\n欄位：{columns.Length}\n度量值：{measures.Length}\n\n選取子項可查看型別或 DAX 定義。",
                [new(id + "/columns", $"欄位（{columns.Length}）", $"{table.Name} 的欄位定義。", columns),
                 new(id + "/measures", $"度量值（{measures.Length}）", $"{table.Name} 的度量值定義；沒有計算或執行。", measures)]);
        }).ToImmutableArray();
        var relationships = model.Relationships.Select(r => new MetadataNode("relationship/" + Uri.EscapeDataString(r.Name), r.Name,
            $"關係：{r.Name}\n來源：{r.FromTable}[{r.FromColumn}]\n目標：{r.ToTable}[{r.ToColumn}]\n啟用：{(r.IsActive ? "是" : "否")}\n來源基數：{r.FromCardinality ?? "未提供"}\n目標基數：{r.ToCardinality ?? "未提供"}\n交叉篩選：{r.CrossFiltering ?? "未提供"}\n安全篩選：{r.SecurityFiltering ?? "未提供"}\n\n僅列出 reader 提供的單欄關係設定；本頁未驗證資料唯一性或模型品質。", [])).ToImmutableArray();
        ImmutableArray<MetadataNode> nodes =
        [
            new("overview", "模型概覽", Summary(model), []),
            new("tables", $"表（{tables.Length}）", "模型中的表、欄位與度量值。", tables),
            new("relationships", $"關係（{relationships.Length}）", "單欄關係定義；沒有執行資料查詢。", relationships)
        ];
        var query = search.Trim();
        if (query.Length == 0) return nodes;
        return nodes.Select(node => Filter(node, query)).OfType<MetadataNode>().ToImmutableArray();
    }

    public static string Summary(ModelSnapshot model) => Limit(
        $"模型：{model.Identity.Name}\n表：{model.Tables.Length}\n欄位：{model.Tables.Sum(t => t.Columns.Length)}\n度量值：{model.Tables.Sum(t => t.Measures.Length)}\n單欄關係：{model.Relationships.Length}\n\n模型 ID：{model.Identity.Id}\n內容版本：{model.Identity.Revision}\n\n此頁只檢視結構。DAX 表達式是原始定義，沒有執行，也沒有送往 AI。");

    private static MetadataNode? Filter(MetadataNode node, string query)
    {
        if (node.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return node;
        var children = node.Children.Select(child => Filter(child, query)).OfType<MetadataNode>().ToImmutableArray();
        return children.IsEmpty ? null : node with { Children = children };
    }

    private static string Limit(string text) => text.Length <= MaxDetailCharacters ? text :
        text[..MaxDetailCharacters] + "\n\n[顯示已截斷；原始檔案未修改。]";
}
