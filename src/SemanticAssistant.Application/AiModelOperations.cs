using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record PendingAiWrite(ModelIdentity Source, ModelChanges Changes, ModelModificationSnapshot Policy,
    DashboardRegistration? Dashboard, ImmutableArray<ToolResult> Evidence, DateTimeOffset ExpiresAt)
{
    public void EnsureValid(ModelModificationPolicy policy)
    {
        policy.EnsureAllowed(Policy);
        if (ExpiresAt <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("AI修改提案已過期。");
        foreach (var evidence in Evidence) ToolPolicyEngine.EnsureResultFresh(evidence);
    }
}

// Tools stage immutable changes. Physical writes are a separate, user-confirmed
// transaction after the AI turn, so no old-version query can pretend to see new objects.
public sealed class AiModelOperations(ISemanticModelReader reader, IModelDefinitionWriter writer, DashboardRegistry registry,
    ModelModificationPolicy modificationPolicy, ReportAppearance appearance)
{
    private readonly ModelModificationSnapshot epoch = modificationPolicy.Snapshot;
    private ModelChanges changes = new([], [], []);
    private ModelIdentity? target;
    private DashboardRegistration? dashboard;
    public bool HasPending => !changes.Measures.IsEmpty || !changes.Columns.IsEmpty || !changes.Relationships.IsEmpty;
    public PendingAiWrite? Seal(ImmutableArray<ToolResult> evidence) => !HasPending ? null :
        new(target!, changes, epoch, dashboard, evidence, DateTimeOffset.UtcNow.AddMinutes(10));

    public async Task<ToolResult> DispatchAsync(ToolCall call, PermissionSnapshot permissions, Action guard, CancellationToken token)
    {
        var before = await writer.ReadDefinitionAsync(token).WaitAsync(token);
        guard(); token.ThrowIfCancellationRequested();
        if (before.Identity != permissions.Target) throw new DefinitionConflictException();
        var mutation = AiModelToolDefinitions.IsMutation(call.Name);
        if (mutation) modificationPolicy.EnsureAllowed(epoch);
        if (call.Name == "list_dashboard_measures")
        {
            StrictJson.Parse<Empty>(call.Arguments.GetRawText());
            return Data(call, registry.List(before).Select(e => new { DashboardId = e.Id, e.Plan.Title, e.Revision,
                Verified = DashboardRegistry.Matches(e, before), e.RecoveredFromIntent, HtmlMeasure = e.HtmlMeasure.Name,
                Metrics = e.Plan.Metrics.Select(m => new { m.Table, m.Name }) }).ToArray());
        }
        if (call.Name == "get_dashboard_definition")
        {
            var args = StrictJson.Parse<DashboardIdArguments>(call.Arguments.GetRawText());
            var entry = registry.Get(ParseId(args.Id), before);
            return Data(call, new { DashboardId = entry.Id, entry.Revision, Verified = DashboardRegistry.Matches(entry, before),
                Plan = DashboardProposals.PlanPayload(entry.Plan), entry.HtmlMeasure, entry.Appearance, entry.RecoveredFromIntent,
                ManagedMeasures = entry.Managed.Select(m => new { m.Table, m.Definition.Name }), SourceVersion = before.Identity.Revision });
        }
        if (call.Name == "get_model_edit_context")
        {
            var name = ToolArguments.RequiredString(call.Arguments, "table", 128);
            var table = before.Tables.SingleOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException("表不存在。");
            var registered = registry.List(before);
            return Data(call, new { table.Name, Measures = table.Measures.Select(m => new { m.Name, m.Expression,
                Owned = m.Owner == DefinitionRules.Owner && m.IntegrityValid, DashboardManaged = registered.Any(e => e.Managed.Any(o =>
                    o.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && o.Definition.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))) }),
                CalculatedColumns = table.CalculatedColumns.Select(c => new { c.Name, c.Expression, c.DataType, Owned = c.Owner == DefinitionRules.Owner && c.IntegrityValid }),
                Relationships = before.Relationships.Where(r => r.Definition.From.Table == table.Name || r.Definition.To.Table == table.Name)
                    .Select(r => new { r.Definition, Owned = r.Owner == DefinitionRules.Owner && r.IntegrityValid }) });
        }
        if (call.Name is "generate_dashboard_measures" or "update_dashboard_measures")
        {
            if (HasPending) throw new ToolInputException("同一回合只能有一個Dashboard部署，且不能混用一般模型修改。");
            DashboardRecord? previous = null;
            DashboardPlan plan;
            if (call.Name == "update_dashboard_measures")
            {
                var args = StrictJson.Parse<DashboardUpdateArguments>(call.Arguments.GetRawText());
                previous = registry.Get(ParseId(args.Id), before);
                if (previous.Revision != args.Revision || !DashboardRegistry.Matches(previous, before)) throw new DefinitionConflictException();
                plan = args.Plan;
            }
            else plan = StrictJson.Parse<DashboardCreateArguments>(call.Arguments.GetRawText()).Plan;
            var model = await reader.ReadSnapshotAsync(token).WaitAsync(token); guard();
            if (model.Identity != before.Identity) throw new DefinitionConflictException();
            DashboardPlans.Validate(plan, model);
            foreach (var metric in plan.Metrics)
            {
                if (before.Tables.SelectMany(t => t.Measures.Select(m => (Table: t.Name, Measure: m))).Any(x =>
                    x.Measure.Name.Equals(metric.Name, StringComparison.OrdinalIgnoreCase) &&
                    !(previous?.Managed.Any(m => m.Table == x.Table && m.Definition.Name == x.Measure.Name) == true)))
                    throw new ToolInputException("新指標名稱已存在或由其他物件管理；請使用新的SA_名稱。");
            }
            var id = previous?.Id ?? Guid.NewGuid();
            var htmlTable = previous?.HtmlMeasure.Table ?? plan.Metrics[0].Table;
            var htmlName = previous?.HtmlMeasure.Name ?? "SA_Dashboard_" + id.ToString("N");
            var projected = DashboardPlans.ProjectMetrics(plan, model);
            // Projection is proposed definitions only, never invented query data.
            var html = DynamicHtmlMeasureCompiler.Compile(plan, projected, htmlTable, htmlName, appearance);
            var candidate = new ModelChanges(plan.Metrics.Add(html), [], []);
            Validate(candidate, before);
            if ((previous?.Managed.Select(m => (m.Table, m.Definition.Name)) ?? []).Concat(candidate.Measures.Select(m => (m.Table, m.Name))).Distinct().Count() > 16)
                throw new ToolInputException("看板保留的自有物件超過16個；不能悄悄刪除舊指標。");
            changes = candidate; target = before.Identity; dashboard = new(id, plan, html, appearance, previous);
        }
        else
        {
            if (dashboard is not null) throw new ToolInputException("Dashboard部署不能混用一般模型修改。");
            ModelChanges candidate = changes;
            var add = call.Name.StartsWith("add_", StringComparison.Ordinal);
            if (call.Name is "add_measure" or "update_measure")
            {
                var args = StrictJson.Parse<MeasureArguments>(call.Arguments.GetRawText());
                var table = Table(before, args.Table);
                var change = new MeasureChange(table.Name, args.Name, args.Expression);
                var existing = before.Tables.SelectMany(t => t.Measures).Any(m => m.Name.Equals(args.Name, StringComparison.OrdinalIgnoreCase));
                RequireAction(add, existing);
                if (registry.IsManaged(before, table.Name, args.Name)) throw new ToolInputException("此度量值由Dashboard管理，請使用update_dashboard_measures。");
                candidate = candidate with { Measures = candidate.Measures.Add(change) };
            }
            else if (call.Name is "add_calculated_column" or "update_calculated_column")
            {
                var args = StrictJson.Parse<ColumnArguments>(call.Arguments.GetRawText());
                var table = Table(before, args.Table);
                RequireAction(add, table.Columns.Any(c => c.Equals(args.Name, StringComparison.OrdinalIgnoreCase)));
                candidate = candidate with { Columns = candidate.Columns.Add(new(table.Name, args.Name, args.Expression, args.Type)) };
            }
            else if (call.Name is "add_relationship" or "update_relationship")
            {
                var args = StrictJson.Parse<RelationshipArguments>(call.Arguments.GetRawText());
                RequireAction(add, before.Relationships.Any(r => r.Definition.Name.Equals(args.Name, StringComparison.OrdinalIgnoreCase)) ||
                    before.UnsupportedRelationshipNames.Contains(args.Name, StringComparer.OrdinalIgnoreCase));
                candidate = candidate with { Relationships = candidate.Relationships.Add(new(args.Name, args.From, args.To, args.Active, args.FromCardinality, args.ToCardinality, args.Filter)) };
            }
            else throw new ToolInputException("未知模型修改工具。");
            Validate(candidate, before); changes = candidate; target = before.Identity;
        }
        guard(); modificationPolicy.EnsureAllowed(epoch);
        return new(call.Id, call.Name, true, "modification_pending", "修改提案已驗證結構，尚未寫入。回答完成後，本機將顯示實際差異，需使用者同意並回讀才算完成。",
            ContractJson.Element(new { Pending = true, Objects = changes.Measures.Length + changes.Columns.Length + changes.Relationships.Length,
                DashboardId = dashboard?.Id }));
    }
    private static DefinitionTable Table(DefinitionState state, string name) => state.Tables.SingleOrDefault(t =>
        t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolInputException("目標表不存在。");
    private static void RequireAction(bool add, bool exists)
    { if (add == exists) throw new ToolInputException(add ? "新增名稱已存在，請用更新工具。" : "更新目標不存在，請用新增工具。"); }
    private static Guid ParseId(string id) => Guid.TryParse(id, out var value) && value != Guid.Empty ? value : throw new ToolInputException("Dashboard識別碼無效。");
    private static void Validate(ModelChanges candidate, DefinitionState before) => DefinitionRules.ValidatePlan(new(Guid.NewGuid(), before.Identity, before.Revision, candidate.Measures)
        { Columns = candidate.Columns, Relationships = candidate.Relationships }, before);
    private static ToolResult Data(ToolCall call, object data) => new(call.Id, call.Name, true, "ok", "已讀取模型／Dashboard定義；沒有修改模型或查詢數值。",
        JsonSerializer.SerializeToElement(data, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
    private sealed record Empty;
    private sealed record DashboardIdArguments([property: JsonRequired, JsonPropertyName("dashboard_id")] string Id);
    private sealed record DashboardCreateArguments([property: JsonRequired, JsonPropertyName("plan")] DashboardPlan Plan);
    private sealed record DashboardUpdateArguments([property: JsonRequired, JsonPropertyName("dashboard_id")] string Id,
        [property: JsonRequired, JsonPropertyName("expected_revision")] string Revision, [property: JsonRequired, JsonPropertyName("plan")] DashboardPlan Plan);
    private sealed record MeasureArguments([property: JsonRequired, JsonPropertyName("table")] string Table,
        [property: JsonRequired, JsonPropertyName("name")] string Name, [property: JsonRequired, JsonPropertyName("expression")] string Expression);
    private sealed record ColumnArguments([property: JsonRequired, JsonPropertyName("table")] string Table,
        [property: JsonRequired, JsonPropertyName("name")] string Name, [property: JsonRequired, JsonPropertyName("expression")] string Expression,
        [property: JsonRequired, JsonPropertyName("data_type")] ColumnValueType Type);
    private sealed record RelationshipArguments([property: JsonRequired, JsonPropertyName("name")] string Name,
        [property: JsonRequired, JsonPropertyName("from")] DashboardField From, [property: JsonRequired, JsonPropertyName("to")] DashboardField To,
        [property: JsonRequired, JsonPropertyName("is_active")] bool Active,
        [property: JsonRequired, JsonPropertyName("from_cardinality")] RelationshipCardinality FromCardinality,
        [property: JsonRequired, JsonPropertyName("to_cardinality")] RelationshipCardinality ToCardinality,
        [property: JsonRequired, JsonPropertyName("cross_filtering")] RelationshipFilter Filter);
}
