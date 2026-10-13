using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Application;

namespace SemanticAssistant.Adapters.PowerBI.Local;

public static class TomObjectMutations
{
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static Table Table(Model model, string name) => model.Tables.Single(t => Same(t.Name, name));
    private static Column Column(Model model, DashboardField field) => Table(model, field.Table).Columns.Single(c => Same(c.Name, field.Column));
    public static OwnedMeasure ReadMeasure(Measure m) => new(m.Name, m.Expression ?? "", Owner(m.Annotations), Operation(m.Annotations))
    { IntegrityStamp = m.Annotations.Find(DefinitionRules.IntegrityAnnotation)?.Value, IntegrityValid = ValidIntegrity(m, m.Annotations) };
    public static void MarkMeasure(Measure m, Guid operation) => Mark(m, m.Annotations, operation);
    public static OwnedCalculatedColumn ReadColumn(CalculatedColumn c) => new(c.Name, c.Expression ?? "", c.DataType.ToString(), Owner(c.Annotations), Operation(c.Annotations))
    { IntegrityStamp = c.Annotations.Find(DefinitionRules.IntegrityAnnotation)?.Value, IntegrityValid = ValidIntegrity(c, c.Annotations) };
    public static OwnedRelationship ReadRelationship(SingleColumnRelationship r) => new(new(r.Name,
        new(r.FromColumn.Table.Name, r.FromColumn.Name), new(r.ToColumn.Table.Name, r.ToColumn.Name), r.IsActive,
        Enum.Parse<RelationshipCardinality>(r.FromCardinality.ToString()), Enum.Parse<RelationshipCardinality>(r.ToCardinality.ToString()),
        Enum.Parse<RelationshipFilter>(r.CrossFilteringBehavior.ToString())), Owner(r.Annotations), Operation(r.Annotations))
    { IntegrityStamp = r.Annotations.Find(DefinitionRules.IntegrityAnnotation)?.Value, IntegrityValid = ValidIntegrity(r, r.Annotations) };
    private static string? Owner(IEnumerable<Annotation> a) => a.FirstOrDefault(x => x.Name == DefinitionRules.OwnerAnnotation)?.Value;
    private static Guid? Operation(IEnumerable<Annotation> a) => Guid.TryParse(a.FirstOrDefault(x => x.Name == DefinitionRules.OperationAnnotation)?.Value, out var id) ? id : null;
    private static string Integrity(MetadataObject obj)
    {
        // Server timestamps, inferred types/state and internal hierarchies are not user definition changes.
        // Use TOM's schema-aware options rather than dropping arbitrary property names from user annotations.
        var json = Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeObject(obj, new SerializeOptions
        { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true });
        if (Encoding.UTF8.GetByteCount(json) > 128_000) throw new InvalidDataException("物件完整性投影超過上限。");
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException();
        if (root["annotations"] is JsonArray annotations)
            for (var i = annotations.Count - 1; i >= 0; i--)
                if (annotations[i]?["name"]?.GetValue<string>() == DefinitionRules.IntegrityAnnotation) annotations.RemoveAt(i);
        using var doc = JsonDocument.Parse(root.ToJsonString());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Canonical(doc.RootElement, writer, null);
        return "definition-v2:" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Canonical(JsonElement value, Utf8JsonWriter writer, string? property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var item in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Name); Canonical(item.Value, writer, item.Name); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var values = value.EnumerateArray().ToArray();
            if (property is "annotations" or "extendedProperties") values = values.OrderBy(v => v.GetProperty("name").GetString(), StringComparer.Ordinal).ToArray();
            foreach (var item in values) Canonical(item, writer, null);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
    private static bool ValidIntegrity(MetadataObject obj, IEnumerable<Annotation> a) =>
        Owner(a) != DefinitionRules.Owner || a.FirstOrDefault(x => x.Name == DefinitionRules.IntegrityAnnotation)?.Value == Integrity(obj);
    private static void Mark<T>(MetadataObject obj, NamedMetadataObjectCollection<Annotation, T> a, Guid operation) where T : MetadataObject
    {
        Set(a, DefinitionRules.OwnerAnnotation, DefinitionRules.Owner); Set(a, DefinitionRules.OperationAnnotation, operation.ToString());
        Set(a, DefinitionRules.IntegrityAnnotation, Integrity(obj));
    }
    private static void Set<T>(NamedMetadataObjectCollection<Annotation, T> annotations, string name, string? value) where T : MetadataObject
    {
        var annotation = annotations.Find(name);
        if (value is null) { if (annotation is not null) annotations.Remove(annotation); }
        else if (annotation is null) annotations.Add(new Annotation { Name = name, Value = value }); else annotation.Value = value;
    }
    private static void RestoreAnnotations<T>(NamedMetadataObjectCollection<Annotation, T> a, string? owner, Guid? operation, string? integrity) where T : MetadataObject
    {
        Set(a, DefinitionRules.OwnerAnnotation, owner); Set(a, DefinitionRules.OperationAnnotation, operation?.ToString()); Set(a, DefinitionRules.IntegrityAnnotation, integrity);
    }
    public static void StageApply(Model model, DefinitionPlan plan)
    {
        foreach (var c in plan.Columns)
        {
            var table = Table(model, c.Table); var existing = table.Columns.SingleOrDefault(x => Same(x.Name, c.Name));
            if (existing is not null && existing is not CalculatedColumn) throw new DefinitionConflictException();
            var column = (CalculatedColumn?)existing;
            if (column is null) { column = new CalculatedColumn { Name = c.Name }; table.Columns.Add(column); }
            column.Expression = c.Expression; column.DataType = Enum.Parse<DataType>(c.DataType.ToString()); Mark(column, column.Annotations, plan.OperationId);
        }
        foreach (var r in plan.Relationships)
        {
            var existing = model.Relationships.SingleOrDefault(x => Same(x.Name, r.Name));
            if (existing is not null && existing is not SingleColumnRelationship) throw new DefinitionConflictException();
            var relationship = (SingleColumnRelationship?)existing;
            if (relationship is null) { relationship = new SingleColumnRelationship { Name = r.Name }; model.Relationships.Add(relationship); }
            Configure(model, relationship, r); Mark(relationship, relationship.Annotations, plan.OperationId);
        }
    }
    private static void Configure(Model model, SingleColumnRelationship target, RelationshipChange change)
    {
        target.FromColumn = Column(model, change.From); target.ToColumn = Column(model, change.To); target.IsActive = change.IsActive;
        target.FromCardinality = Enum.Parse<RelationshipEndCardinality>(change.FromCardinality.ToString());
        target.ToCardinality = Enum.Parse<RelationshipEndCardinality>(change.ToCardinality.ToString());
        target.CrossFilteringBehavior = Enum.Parse<CrossFilteringBehavior>(change.CrossFiltering.ToString());
    }
    public static void StageRestore(Model model, DefinitionPreview preview, DefinitionState current)
    {
        ValidateRemovalDependencies(model, preview);
        // Restore relationship endpoints before removing a newly-created column.
        foreach (var r in preview.Plan.Relationships)
        {
            var before = DefinitionRules.FindRelationship(preview.Before, r);
            if (before == DefinitionRules.FindRelationship(current, r)) continue;
            var existing = model.Relationships.Single(x => Same(x.Name, r.Name));
            if (before is null) model.Relationships.Remove(existing);
            else
            {
                var relationship = (SingleColumnRelationship)existing; Configure(model, relationship, before.Definition);
                RestoreAnnotations(relationship.Annotations, before.Owner, before.OperationId, before.IntegrityStamp);
            }
        }
        foreach (var c in preview.Plan.Columns)
        {
            var before = DefinitionRules.FindColumn(preview.Before, c);
            if (before == DefinitionRules.FindColumn(current, c)) continue;
            var table = Table(model, c.Table); var existing = table.Columns.Single(x => Same(x.Name, c.Name));
            if (before is null) table.Columns.Remove(existing);
            else
            {
                var column = (CalculatedColumn)existing; column.Expression = before.Expression; column.DataType = Enum.Parse<DataType>(before.DataType);
                RestoreAnnotations(column.Annotations, before.Owner, before.OperationId, before.IntegrityStamp);
            }
        }
    }
    private static void ValidateRemovalDependencies(Model model, DefinitionPreview preview)
    {
        foreach (var change in preview.Plan.Columns.Where(c => DefinitionRules.FindColumn(preview.Before, c) is null))
        {
            var table = Table(model, change.Table); var column = table.Columns.SingleOrDefault(c => Same(c.Name, change.Name));
            if (column is null) continue;
            var reference = "[" + change.Name.Replace("]", "]]") + "]";
            bool References(string? expression) => expression?.Contains(reference, StringComparison.OrdinalIgnoreCase) == true;
            if (model.Tables.SelectMany(t => t.Columns).Any(c => c.SortByColumn == column) ||
                model.Tables.SelectMany(t => t.Hierarchies).SelectMany(h => h.Levels).Any(l => l.Column == column) ||
                model.Tables.Any(t => t.Measures.Any(m => References(m.Expression) && !preview.Plan.Changes.Any(p => Same(p.Table, t.Name) && Same(p.Name, m.Name)))) ||
                model.Tables.Any(t => t.Columns.OfType<CalculatedColumn>().Any(c => References(c.Expression) && !preview.Plan.Columns.Any(p => Same(p.Table, t.Name) && Same(p.Name, c.Name)))) ||
                model.Roles.SelectMany(r => r.TablePermissions).Any(p => References(p.FilterExpression))) throw new DefinitionConflictException();
        }
        foreach (var change in preview.Plan.Relationships.Where(r => DefinitionRules.FindRelationship(preview.Before, r) is null))
        {
            var relationship = model.Relationships.SingleOrDefault(r => Same(r.Name, change.Name));
            if (relationship is not null && model.Tables.SelectMany(t => t.Columns).SelectMany(c => c.Variations).Any(v => v.Relationship == relationship))
                throw new DefinitionConflictException();
        }
        foreach (var change in preview.Plan.Changes.Where(c => DefinitionRules.Find(preview.Before, c) is null))
        {
            var reference = "[" + change.Name.Replace("]", "]]") + "]";
            bool References(string? expression) => expression?.Contains(reference, StringComparison.OrdinalIgnoreCase) == true;
            if (model.Tables.Any(t => t.Measures.Any(m => References(m.Expression) && !preview.Plan.Changes.Any(p => Same(p.Table, t.Name) && Same(p.Name, m.Name)))) ||
                model.Tables.Any(t => t.Columns.OfType<CalculatedColumn>().Any(c => References(c.Expression) && !preview.Plan.Columns.Any(p => Same(p.Table, t.Name) && Same(p.Name, c.Name)))) ||
                model.Roles.SelectMany(r => r.TablePermissions).Any(p => References(p.FilterExpression))) throw new DefinitionConflictException();
        }
    }
}
