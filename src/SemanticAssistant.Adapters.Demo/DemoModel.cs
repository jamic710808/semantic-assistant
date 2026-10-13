using System.Collections.Immutable;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.Demo;

public sealed class DemoModel : ISemanticModelReader, IReadOnlyDaxQueryRunner
{
    public const string SupportedQuery = "EVALUATE SUMMARIZECOLUMNS('日期'[月份], \"銷售額\", [銷售額])";
    public static ModelSnapshot Snapshot { get; } = new(new("demo-sales", "合成銷售模型", "demo-v1", true),
    [
        new("銷售", [new("日期", "DateTime"), new("金額", "Decimal")], [new("銷售額", "SUM('銷售'[金額])")]),
        new("日期", [new("月份", "String")], [])
    ]);

    public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }

    public Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target != Snapshot.Identity || dax != SupportedQuery)
            throw new InvalidOperationException("示範 adapter 僅接受固定的合成查詢。");
        ImmutableArray<ImmutableArray<System.Text.Json.JsonElement>> rows =
        [
            [ContractJson.Element("2026-01"), ContractJson.Element(120_000)],
            [ContractJson.Element("2026-02"), ContractJson.Element(145_000)],
            [ContractJson.Element("2026-03"), ContractJson.Element(132_000)]
        ];
        return Task.FromResult(new QueryResult(Guid.NewGuid().ToString("N"), target, ["月份", "銷售額"],
            rows.Take(limits.MaxRows).ToImmutableArray(), rows.Length > limits.MaxRows, dax));
    }
}
