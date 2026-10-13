using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.Adapters.PowerBI.Local;

public static class TomModelProjection
{
    public static ModelSnapshot Read(Database db, string modelId)
    {
        var tables = db.Model.Tables.Select(t => new TableDefinition(t.Name,
            t.Columns.Where(c => c is not RowNumberColumn).Select(c => new ColumnDefinition(c.Name, c.DataType.ToString(), c is CalculatedColumn cc ? cc.Expression : null)).ToImmutableArray(),
            t.Measures.Select(m => new MeasureDefinition(m.Name, m.Expression ?? "")).ToImmutableArray())).ToImmutableArray();
        var relationships = db.Model.Relationships.OfType<SingleColumnRelationship>().Select(r => new RelationshipDefinition(
            r.Name, r.FromColumn.Table.Name, r.FromColumn.Name, r.ToColumn.Table.Name, r.ToColumn.Name, r.IsActive,
            r.FromCardinality.ToString(), r.ToCardinality.ToString(), r.CrossFilteringBehavior.ToString(), r.SecurityFilteringBehavior.ToString())).ToImmutableArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { tables, relationships });
        if (bytes.Length > 2_000_000) throw new InvalidDataException("模型投影超過上限。");
        // Consent must also become stale when partitions, sources, roles or hidden
        // definition properties change. Only the digest leaves this method.
        var full = Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(db, new SerializeOptions
        { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true });
        if (Encoding.UTF8.GetByteCount(full) > 8_388_608) throw new InvalidDataException("完整模型定義超過上限。");
        var fullBytes = Encoding.UTF8.GetBytes(full);
        return new(new(modelId, db.Name, Convert.ToHexString(SHA256.HashData(fullBytes)).ToLowerInvariant(), false), tables)
        { Relationships = relationships };
    }
}

public sealed class TomWriterConnection(Server server, Database database, string modelId, Action ensureSource) : IDisposable
{
    public Database Database => database;
    public void EnsureSource() => ensureSource();
    public DefinitionState Read()
    {
        EnsureSource();
        var model = TomModelProjection.Read(database, modelId);
        var tables = database.Model.Tables.Select(t => new DefinitionTable(t.Name, t.Columns.Select(c => c.Name).ToImmutableArray(),
            t.Measures.Select(TomObjectMutations.ReadMeasure).ToImmutableArray())
        {
            CalculatedColumns = t.Columns.OfType<CalculatedColumn>().Select(TomObjectMutations.ReadColumn).ToImmutableArray(),
            ColumnTypes = t.Columns.ToImmutableDictionary(c => c.Name, c => c.DataType.ToString(), StringComparer.OrdinalIgnoreCase)
        }).ToImmutableArray();
        return new(model.Identity, model.Identity.Revision, tables)
        {
            Relationships = database.Model.Relationships.OfType<SingleColumnRelationship>().Select(TomObjectMutations.ReadRelationship).ToImmutableArray(),
            UnsupportedRelationshipNames = database.Model.Relationships.Where(r => r is not SingleColumnRelationship).Select(r => r.Name).ToImmutableArray()
        };
    }
    public void Dispose() => server.Dispose();
}

// A distinct adapter: read-only query clients never expose this writer object to an AI provider.
public sealed class TomDefinitionWriter(Func<CancellationToken, Task<TomWriterConnection>> open,
    Func<CancellationToken, Task<TomWriterConnection>>? openWrite = null,
    Func<CancellationToken, Task<TomWriterConnection>>? openRestore = null) : IModelDefinitionWriter
{
    public async Task<DefinitionState> ReadDefinitionAsync(CancellationToken token)
    {
        using var connection = await open(token);
        return await Task.Run(() => { token.ThrowIfCancellationRequested(); return connection.Read(); }, token);
    }
    public async Task ApplyAsync(DefinitionPlan plan, Action ensureAuthorized, CancellationToken token)
    {
        ensureAuthorized(); token.ThrowIfCancellationRequested();
        using var connection = await (openWrite ?? open)(token);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); ensureAuthorized(); var state = connection.Read();
            DefinitionRules.ValidatePlan(plan, state);
            foreach (var change in plan.Changes)
            {
                var table = connection.Database.Model.Tables.Single(t => t.Name.Equals(change.Table, StringComparison.OrdinalIgnoreCase));
                var measure = table.Measures.SingleOrDefault(m => m.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
                if (measure is null) { measure = new Measure { Name = change.Name }; table.Measures.Add(measure); }
                measure.Expression = change.Expression;
                TomObjectMutations.MarkMeasure(measure, plan.OperationId);
            }
            TomObjectMutations.StageApply(connection.Database.Model, plan);
            token.ThrowIfCancellationRequested(); connection.EnsureSource(); ensureAuthorized();
            if (!plan.Columns.IsEmpty) connection.Database.Model.RequestRefresh(RefreshType.Calculate);
            connection.Database.Model.SaveChanges(); // No claim of external atomicity or compare-and-swap.
            token.ThrowIfCancellationRequested(); connection.EnsureSource();
        }, token);
    }
    public async Task RestoreAsync(DefinitionPreview preview, DefinitionState observed, CancellationToken token)
    {
        using var connection = await (openRestore ?? openWrite ?? open)(token);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); var current = connection.Read();
            if (current.Revision != observed.Revision) throw new DefinitionConflictException();
            DefinitionRules.ValidateRollback(preview, current);
            TomObjectMutations.StageRestore(connection.Database.Model, preview, current);
            foreach (var change in preview.Plan.Changes)
            {
                var table = connection.Database.Model.Tables.Single(t => t.Name.Equals(change.Table, StringComparison.OrdinalIgnoreCase));
                var measure = table.Measures.SingleOrDefault(m => m.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
                var before = DefinitionRules.Find(preview.Before, change);
                if (DefinitionRules.SameMeasure(before, DefinitionRules.Find(current, change))) continue;
                if (before is null && measure is not null) table.Measures.Remove(measure);
                else if (before is not null && measure is not null)
                {
                    measure.Expression = before.Expression;
                    Set(measure, DefinitionRules.OwnerAnnotation, before.Owner!);
                    if (before.OperationId is { } operation) Set(measure, DefinitionRules.OperationAnnotation, operation.ToString());
                    else if (measure.Annotations.Find(DefinitionRules.OperationAnnotation) is { } annotation) measure.Annotations.Remove(annotation);
                    if (before.IntegrityStamp is { } stamp) Set(measure, DefinitionRules.IntegrityAnnotation, stamp);
                    else if (measure.Annotations.Find(DefinitionRules.IntegrityAnnotation) is { } integrity) measure.Annotations.Remove(integrity);
                }
            }
            token.ThrowIfCancellationRequested(); connection.EnsureSource();
            if (!preview.Plan.Columns.IsEmpty) connection.Database.Model.RequestRefresh(RefreshType.Calculate);
            connection.Database.Model.SaveChanges();
            token.ThrowIfCancellationRequested();
        }, token);
    }
    private static void Set(Measure measure, string name, string value)
    {
        var annotation = measure.Annotations.Find(name);
        if (annotation is null) measure.Annotations.Add(new Annotation { Name = name, Value = value }); else annotation.Value = value;
    }
}
