using System.Collections.Immutable;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.Demo;

public sealed class DemoDefinitionWriter : IModelDefinitionWriter, ISemanticModelReader, IReadOnlyDaxQueryRunner
{
    private readonly object sync = new();
    private DefinitionState current = new(DemoModel.Snapshot.Identity, "demo-definition-0",
        DemoModel.Snapshot.Tables.Select(t => new DefinitionTable(t.Name, t.Columns.Select(c => c.Name).ToImmutableArray(),
            t.Measures.Select(m => new OwnedMeasure(m.Name, m.Expression, null, null)).ToImmutableArray())
        { ColumnTypes = t.Columns.ToImmutableDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase) }).ToImmutableArray());
    private long revision;
    public Task<DefinitionState> ReadDefinitionAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); lock (sync) return Task.FromResult(current); }
    public Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested(); ensureAuthorized(); DefinitionRules.ValidatePlan(plan, current);
            var tables = current.Tables.Select(t => t with
            {
                Measures = t.Measures.Where(m => !plan.Changes.Any(c =>
                c.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                .Concat(plan.Changes.Where(c => c.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new OwnedMeasure(c.Name, c.Expression, DefinitionRules.Owner, plan.OperationId))).ToImmutableArray()
            }).ToImmutableArray();
            tables = ApplyColumns(tables, plan);
            var relationships = current.Relationships.Where(r => !plan.Relationships.Any(c => Same(c.Name, r.Definition.Name)))
                .Concat(plan.Relationships.Select(r => new OwnedRelationship(r, DefinitionRules.Owner, plan.OperationId))).ToImmutableArray();
            ensureAuthorized(); token.ThrowIfCancellationRequested();
            current = current with { Tables = tables, Relationships = relationships, Revision = $"demo-definition-{++revision}", Identity = current.Identity with { Revision = $"demo-metadata-{revision}" } };
        }
        return Task.CompletedTask;
    }
    public Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (current.Revision != observed.Revision) throw new DefinitionConflictException();
            DefinitionRules.ValidateRollback(preview, current);
            var tables = current.Tables.Select(t => t with
            {
                Measures = t.Measures.Where(m => !preview.Plan.Changes.Any(c =>
                c.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                .Concat(preview.Before.Tables.Single(b => b.Name == t.Name).Measures.Where(m => preview.Plan.Changes.Any(c =>
                    c.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))).ToImmutableArray()
            }).ToImmutableArray();
            tables = RestoreColumns(tables, preview);
            var relationships = current.Relationships.Where(r => !preview.Plan.Relationships.Any(c => Same(c.Name, r.Definition.Name)))
                .Concat(preview.Before.Relationships.Where(r => preview.Plan.Relationships.Any(c => Same(c.Name, r.Definition.Name)))).ToImmutableArray();
            current = current with { Tables = tables, Relationships = relationships, Revision = $"demo-definition-{++revision}", Identity = current.Identity with { Revision = $"demo-metadata-{revision}" } };
        }
        return Task.CompletedTask;
    }
    public async Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var state = await ReadDefinitionAsync(token);
        return new(state.Identity, state.Tables.Select(t => new TableDefinition(t.Name,
            t.Columns.Select(c => new ColumnDefinition(c, t.ColumnTypes[c], t.CalculatedColumns.SingleOrDefault(x => Same(x.Name, c))?.Expression)).ToImmutableArray(),
            t.Measures.Select(m => new MeasureDefinition(m.Name, m.Expression)).ToImmutableArray())).ToImmutableArray())
        {
            Relationships = state.Relationships.Select(r => new RelationshipDefinition(r.Definition.Name, r.Definition.From.Table,
            r.Definition.From.Column, r.Definition.To.Table, r.Definition.To.Column, r.Definition.IsActive,
            r.Definition.FromCardinality.ToString(), r.Definition.ToCardinality.ToString(), r.Definition.CrossFiltering.ToString(), "OneDirection")).ToImmutableArray()
        };
    }
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static ImmutableArray<DefinitionTable> ApplyColumns(ImmutableArray<DefinitionTable> tables, DefinitionPlan plan) => tables.Select(t =>
    {
        var changes = plan.Columns.Where(c => Same(c.Table, t.Name)).ToArray();
        var calculated = t.CalculatedColumns.Where(c => !changes.Any(x => Same(x.Name, c.Name)))
            .Concat(changes.Select(c => new OwnedCalculatedColumn(c.Name, c.Expression, c.DataType.ToString(), DefinitionRules.Owner, plan.OperationId))).ToImmutableArray();
        return t with
        {
            Columns = t.Columns.Where(c => !changes.Any(x => Same(x.Name, c))).Concat(changes.Select(c => c.Name)).ToImmutableArray(),
            ColumnTypes = t.ColumnTypes.RemoveRange(changes.Select(c => c.Name)).SetItems(changes.Select(c => KeyValuePair.Create(c.Name, c.DataType.ToString()))),
            CalculatedColumns = calculated
        };
    }).ToImmutableArray();
    private static ImmutableArray<DefinitionTable> RestoreColumns(ImmutableArray<DefinitionTable> tables, DefinitionPreview preview) => tables.Select(t =>
    {
        var names = preview.Plan.Columns.Where(c => Same(c.Table, t.Name)).Select(c => c.Name).ToArray();
        var before = preview.Before.Tables.Single(b => Same(b.Name, t.Name));
        var prior = before.CalculatedColumns.Where(c => names.Any(n => Same(n, c.Name))).ToArray();
        return t with
        {
            Columns = t.Columns.Where(c => !names.Any(n => Same(n, c))).Concat(prior.Select(c => c.Name)).ToImmutableArray(),
            ColumnTypes = t.ColumnTypes.RemoveRange(names).SetItems(prior.Select(c => KeyValuePair.Create(c.Name, c.DataType))),
            CalculatedColumns = t.CalculatedColumns.Where(c => !names.Any(n => Same(n, c.Name))).Concat(prior).ToImmutableArray()
        };
    }).ToImmutableArray();
    public async Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token)
    {
        var state = await ReadDefinitionAsync(token);
        if (target != state.Identity) throw new DefinitionConflictException();
        if (dax == DashboardPlans.BuildQuery(DemoDashboard.Plan))
        {
            var measure = DefinitionRules.Find(state, DemoDashboard.Plan.Metrics[0]);
            if (measure?.Owner != DefinitionRules.Owner || measure.Expression != DemoDashboard.Plan.Metrics[0].Expression)
                throw new DefinitionConflictException();
            var fixture = await new DemoModel().ExecuteAsync(DemoModel.Snapshot.Identity, DemoModel.SupportedQuery, limits, token);
            return fixture with { Source = target, Query = dax, Columns = ["月份", "SA_合成銷售"] };
        }
        var result = await new DemoModel().ExecuteAsync(DemoModel.Snapshot.Identity, dax, limits, token);
        return result with { Source = target }; // Only the original fixed synthetic query; no DAX expression evaluation.
    }
}
