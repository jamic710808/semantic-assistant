using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;

namespace SemanticAssistant.Application;

public enum ColumnValueType { String, Int64, Double, Decimal, Boolean, DateTime }
public enum RelationshipCardinality { One, Many }
public enum RelationshipFilter { OneDirection, BothDirections, Automatic }
public sealed record CalculatedColumnChange([property: JsonRequired] string Table, [property: JsonRequired] string Name,
    [property: JsonRequired] string Expression, [property: JsonRequired] ColumnValueType DataType);
public sealed record RelationshipChange([property: JsonRequired] string Name, [property: JsonRequired] DashboardField From,
    [property: JsonRequired] DashboardField To, [property: JsonRequired] bool IsActive,
    [property: JsonRequired] RelationshipCardinality FromCardinality, [property: JsonRequired] RelationshipCardinality ToCardinality,
    [property: JsonRequired] RelationshipFilter CrossFiltering);
public sealed record ModelChanges([property: JsonRequired] ImmutableArray<MeasureChange> Measures,
    [property: JsonRequired] ImmutableArray<CalculatedColumnChange> Columns, [property: JsonRequired] ImmutableArray<RelationshipChange> Relationships);
public sealed record OwnedCalculatedColumn(string Name, string Expression, string DataType, string? Owner, Guid? OperationId)
{
    public bool IntegrityValid { get; init; } = true;
    public string? IntegrityStamp { get; init; }
}
public sealed record OwnedRelationship(RelationshipChange Definition, string? Owner, Guid? OperationId)
{
    public bool IntegrityValid { get; init; } = true;
    public string? IntegrityStamp { get; init; }
}

public static partial class DefinitionRules
{
    public const string IntegrityAnnotation = "SemanticAssistant.ObjectIntegrity";
    public static int ObjectCount(DefinitionPlan plan) => plan.Changes.Length + plan.Columns.Length + plan.Relationships.Length;
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && !name.Any(char.IsControl);
    public static OwnedCalculatedColumn? FindColumn(DefinitionState state, CalculatedColumnChange change) => state.Tables.SingleOrDefault(t => Same(t.Name, change.Table))?
        .CalculatedColumns.SingleOrDefault(c => Same(c.Name, change.Name));
    public static OwnedRelationship? FindRelationship(DefinitionState state, RelationshipChange change) => state.Relationships.SingleOrDefault(r => Same(r.Definition.Name, change.Name));
    private static void ValidateObjects(DefinitionPlan plan, DefinitionState before)
    {
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan.Columns)
        {
            if (c is null || !ValidName(c.Table) || !ValidName(c.Name) || !columnNames.Add(c.Table + "\0" + c.Name) || !Enum.IsDefined(c.DataType) ||
                string.IsNullOrWhiteSpace(c.Expression) || c.Expression.Length > 8000 || c.Expression.Contains('\0')) throw new ToolInputException("計算欄位參數無效或重複。");
            var table = before.Tables.SingleOrDefault(t => Same(t.Name, c.Table)) ?? throw new ToolInputException("計算欄位目標表不存在。");
            var existing = FindColumn(before, c);
            if ((table.Columns.Any(n => Same(n, c.Name)) && existing is null) || existing is { Owner: not Owner } || existing?.IntegrityValid == false ||
                table.Measures.Any(m => Same(m.Name, c.Name)) || plan.Changes.Any(m => Same(m.Table, c.Table) && Same(m.Name, c.Name)))
                throw new DefinitionConflictException();
        }
        var relationshipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in plan.Relationships)
        {
            if (r is null || !ValidName(r.Name) || !relationshipNames.Add(r.Name) || r.From is null || r.To is null ||
                !Enum.IsDefined(r.FromCardinality) || !Enum.IsDefined(r.ToCardinality) || !Enum.IsDefined(r.CrossFiltering) || Same(r.From.Table, r.To.Table))
                throw new ToolInputException("關係參數無效或重複；兩端需不同表。");
            if (FieldType(r.From, plan, before) != FieldType(r.To, plan, before)) throw new DefinitionConflictException();
            if (before.UnsupportedRelationshipNames.Any(n => Same(n, r.Name))) throw new DefinitionConflictException();
            var existing = FindRelationship(before, r);
            if (existing is not null && (existing.Owner != Owner || !existing.IntegrityValid)) throw new DefinitionConflictException();
            if (before.Relationships.Any(x => !Same(x.Definition.Name, r.Name) && SameEndpoints(x.Definition, r)) ||
                plan.Relationships.Any(x => x != r && SameEndpoints(x, r))) throw new DefinitionConflictException();
        }
        foreach (var existing in before.Relationships)
        {
            if (plan.Relationships.Any(r => Same(r.Name, existing.Definition.Name))) continue;
            if (plan.Columns.Any(c => Same(c.Table, existing.Definition.From.Table) && Same(c.Name, existing.Definition.From.Column) ||
                Same(c.Table, existing.Definition.To.Table) && Same(c.Name, existing.Definition.To.Column)) &&
                FieldType(existing.Definition.From, plan, before) != FieldType(existing.Definition.To, plan, before)) throw new DefinitionConflictException();
        }
    }
    private static string FieldType(DashboardField f, DefinitionPlan plan, DefinitionState before)
    {
        if (!ValidName(f.Table) || !ValidName(f.Column)) throw new ToolInputException("關係欄位名稱無效。");
        var table = before.Tables.SingleOrDefault(t => Same(t.Name, f.Table)) ?? throw new ToolInputException("關係表不存在。");
        var requested = plan.Columns.SingleOrDefault(c => Same(c.Table, f.Table) && Same(c.Name, f.Column));
        if (requested is not null) return requested.DataType.ToString();
        var field = table.ColumnTypes.SingleOrDefault(x => Same(x.Key, f.Column));
        if (field.Key is null || !table.Columns.Any(n => Same(n, f.Column))) throw new ToolInputException("關係欄位不存在或缺少型別資訊。");
        return field.Value;
    }
    private static bool SameEndpoints(RelationshipChange a, RelationshipChange b) =>
        (Same(a.From.Table, b.From.Table) && Same(a.From.Column, b.From.Column) && Same(a.To.Table, b.To.Table) && Same(a.To.Column, b.To.Column)) ||
        (Same(a.From.Table, b.To.Table) && Same(a.From.Column, b.To.Column) && Same(a.To.Table, b.From.Table) && Same(a.To.Column, b.From.Column));
    private static bool ColumnApplied(CalculatedColumnChange c, Guid operation, OwnedCalculatedColumn? now) => now is not null &&
        now.Expression == c.Expression && now.DataType == c.DataType.ToString() && now.Owner == Owner && now.OperationId == operation && now.IntegrityValid;
    private static bool RelationshipApplied(RelationshipChange r, Guid operation, OwnedRelationship? now) => now is not null &&
        EquivalentRelationship(now.Definition, r) && now.Owner == Owner && now.OperationId == operation && now.IntegrityValid;
    public static bool EquivalentRelationship(RelationshipChange a, RelationshipChange b) => Same(a.Name, b.Name) &&
        Same(a.From.Table, b.From.Table) && Same(a.From.Column, b.From.Column) && Same(a.To.Table, b.To.Table) && Same(a.To.Column, b.To.Column) &&
        a.FromCardinality == b.FromCardinality && a.ToCardinality == b.ToCardinality && a.CrossFiltering == b.CrossFiltering && a.IsActive == b.IsActive;
    private static bool ObjectsApplied(DefinitionPlan plan, DefinitionState state) =>
        plan.Columns.All(c => ColumnApplied(c, plan.OperationId, FindColumn(state, c))) &&
        plan.Relationships.All(r => RelationshipApplied(r, plan.OperationId, FindRelationship(state, r)));
    private static bool ObjectsBefore(DefinitionPreview p, DefinitionState state) =>
        p.Plan.Columns.All(c => FindColumn(p.Before, c) == FindColumn(state, c)) &&
        p.Plan.Relationships.All(r => FindRelationship(p.Before, r) == FindRelationship(state, r));
    private static void ValidateObjectRollback(DefinitionPreview p, DefinitionState state)
    {
        foreach (var c in p.Plan.Columns)
            if (FindColumn(p.Before, c) != FindColumn(state, c) && !ColumnApplied(c, p.Plan.OperationId, FindColumn(state, c))) throw new DefinitionConflictException();
        foreach (var r in p.Plan.Relationships)
            if (FindRelationship(p.Before, r) != FindRelationship(state, r) && !RelationshipApplied(r, p.Plan.OperationId, FindRelationship(state, r))) throw new DefinitionConflictException();
        foreach (var c in p.Plan.Columns.Where(c => FindColumn(p.Before, c) is null))
            if (state.Relationships.Any(r => !p.Plan.Relationships.Any(x => Same(x.Name, r.Definition.Name)) &&
                (Same(r.Definition.From.Table, c.Table) && Same(r.Definition.From.Column, c.Name) || Same(r.Definition.To.Table, c.Table) && Same(r.Definition.To.Column, c.Name))))
                throw new DefinitionConflictException();
    }
    public static void AppendObjectDiff(DefinitionPlan plan, DefinitionState before, StringBuilder diff)
    {
        foreach (var c in plan.Columns)
            diff.Append($"\n計算欄位 {c.Table}[{c.Name}] · {FindColumn(before, c)?.DataType ?? "（新物件）"} → {c.DataType}\n- {FindColumn(before, c)?.Expression ?? "（不存在）"}\n+ {c.Expression}\n需引擎 Calculate Refresh。\n");
        foreach (var r in plan.Relationships)
            diff.Append($"\n關係 {r.Name}\n- {FindRelationship(before, r)?.Definition.ToString() ?? "（不存在）"}\n+ {r.From.Table}[{r.From.Column}] ({r.FromCardinality}) → {r.To.Table}[{r.To.Column}] ({r.ToCardinality})\n啟用={r.IsActive}，交叉篩選={r.CrossFiltering}；不改 RLS security filtering。\n");
    }
}
