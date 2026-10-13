using SemanticAssistant.Application;

namespace SemanticAssistant.Adapters.Demo;

// One known synthetic plan. This is not a general DAX interpreter.
public static class DemoDashboard
{
    public static DashboardPlan Plan { get; } = new(1, "合成銷售 Dashboard", new("日期", "月份"), null,
        [new("銷售", "SA_合成銷售", "SUM('銷售'[金額])")],
        [new("table", "月份與銷售", VisualKind.Table, null, null, null),
         new("total", "回傳月份銷售合計", VisualKind.Kpi, null, 1, Aggregation.Sum),
         new("trend", "月份銷售分布", VisualKind.BarChart, 0, 1, null)]);
}
