using SemanticAssistant.Application;

namespace SemanticAssistant.Adapters.Demo;

public static class DemoModelChanges
{
    // Metadata fixture only. Actual calculation/cardinality is performed by Power BI, not this adapter.
    public static ModelChanges Mixed { get; } = new(
        [new("銷售", "SA_混合批次", "1")],
        [new("銷售", "SA_月份鍵", "FORMAT('銷售'[日期], \"yyyy-MM\")", ColumnValueType.String)],
        [new("SA_月份關係", new("銷售", "SA_月份鍵"), new("日期", "月份"), false,
            RelationshipCardinality.Many, RelationshipCardinality.One, RelationshipFilter.OneDirection)]);
}
