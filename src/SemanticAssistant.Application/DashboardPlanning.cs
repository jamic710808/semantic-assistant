using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record DashboardField([property: JsonRequired] string Table, [property: JsonRequired] string Column);
// End is exclusive; period filtering is explicit, never inferred from a label.
public sealed record DashboardPeriod([property: JsonRequired] DashboardField Field,
    [property: JsonRequired] DateOnly Start, [property: JsonRequired] DateOnly End);
public sealed record DashboardPlan([property: JsonRequired] int Version, [property: JsonRequired] string Title,
    [property: JsonRequired] DashboardField Grouping, DashboardPeriod? Period,
    [property: JsonRequired] ImmutableArray<MeasureChange> Metrics,
    [property: JsonRequired] ImmutableArray<VisualSpec> Visuals);
public sealed record DashboardDraft(Guid Id, DashboardPlan Plan, DefinitionPreview Definition, string Query, string Hash);

public static class DashboardPlans
{
    public static DashboardPlan Parse(string json) => StrictJson.Parse<DashboardPlan>(json);
    public static ModelSnapshot ProjectMetrics(DashboardPlan plan, ModelSnapshot model) => model with
    {
        Tables = model.Tables.Select(t => t with { Measures = t.Measures
            .Where(m => !plan.Metrics.Any(c => c.Table == t.Name && c.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
            .Concat(plan.Metrics.Where(c => c.Table == t.Name).Select(c => new MeasureDefinition(c.Name, c.Expression))).ToImmutableArray() }).ToImmutableArray()
    };
    public static void Validate(DashboardPlan plan, ModelSnapshot model)
    {
        if (plan.Version != 1 || string.IsNullOrWhiteSpace(plan.Title) || plan.Title.Length > 200 || plan.Grouping is null ||
            plan.Metrics.IsDefaultOrEmpty || plan.Metrics.Length > 6 || plan.Metrics.Any(m => m is null) ||
            plan.Visuals.IsDefaultOrEmpty || plan.Visuals.Length > 8) throw new InvalidDataException("Dashboard 計畫無效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in plan.Metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Table) || metric.Table.Length > 128 || model.Tables.All(t => t.Name != metric.Table) ||
                string.IsNullOrWhiteSpace(metric.Name) || metric.Name.Length > 128 || metric.Name.Any(char.IsControl) || !names.Add(metric.Name) ||
                string.IsNullOrWhiteSpace(metric.Expression) || metric.Expression.Length > 8000 || metric.Expression.Contains('\0'))
                throw new InvalidDataException("Dashboard指標的表、名稱或DAX無效。");
        }
        FindColumn(plan.Grouping, model);
        if (plan.Period is { } period)
        {
            if (period.Field is null || FindColumn(period.Field, model).DataType is not ("DateTime" or "Date") ||
                period.Start.Year < 1900 || period.End.Year > 9998 || period.End <= period.Start ||
                period.End.DayNumber - period.Start.DayNumber > 3660) throw new InvalidDataException("週期需要有效日期欄與開始／排除結束日，範圍最多十年。");
        }
        // Validate indices and numeric visuals before a write using shape-only synthetic values.
        // These placeholders never become report data or leave this validator.
        var shape = new QueryResult("shape", model.Identity, Enumerable.Range(0, plan.Metrics.Length + 1).Select(i => $"c{i}").ToImmutableArray(),
            [Enumerable.Range(0, plan.Metrics.Length + 1).Select(i => i == 0 ? ContractJson.Element("label") : ContractJson.Element(0)).ToImmutableArray()], false, "shape-validation-only");
        ReportSpecs.Validate(new(1, shape.ResultId, plan.Title, plan.Visuals), shape);
        if (plan.Visuals.Any(v => v.Kind is VisualKind.BarChart or VisualKind.LineChart && v.LabelColumn != 0))
            throw new InvalidDataException("Dashboard 圖表標籤必須引用分組欄位。");
    }
    private static ColumnDefinition FindColumn(DashboardField field, ModelSnapshot model)
    {
        if (string.IsNullOrWhiteSpace(field.Table) || field.Table.Length > 128 || string.IsNullOrWhiteSpace(field.Column) || field.Column.Length > 128)
            throw new InvalidDataException("維度／週期欄位名稱無效。");
        return model.Tables.SingleOrDefault(t => t.Name == field.Table)?.Columns.SingleOrDefault(c => c.Name == field.Column)
            ?? throw new InvalidDataException("維度／週期欄位不存在。");
    }
    public static string Field(DashboardField field) => $"'{field.Table.Replace("'", "''")}'[{field.Column.Replace("]", "]]")}]";
    private static string Date(DateOnly value) => string.Create(CultureInfo.InvariantCulture, $"DATE({value.Year},{value.Month},{value.Day})");
    public static string BuildQuery(DashboardPlan plan)
    {
        var parts = new List<string> { Field(plan.Grouping) };
        if (plan.Period is { } p)
            parts.Add($"FILTER(ALL({Field(p.Field)}), {Field(p.Field)} >= {Date(p.Start)} && {Field(p.Field)} < {Date(p.End)})");
        parts.AddRange(plan.Metrics.Select(m => "\"" + m.Name.Replace("\"", "\"\"") + "\", [" + m.Name.Replace("]", "]]") + "]"));
        return "EVALUATE SUMMARIZECOLUMNS(" + string.Join(", ", parts) + ") ORDER BY " + Field(plan.Grouping);
    }
    public static string Hash(DashboardPlan plan, DefinitionPreview definition, string query) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { plan, Definition = definition.PlanHash, query })));
    public static ReportSpec Report(DashboardPlan plan, QueryResult result)
    {
        if (result.Columns.Length != plan.Metrics.Length + 1 && !(result.Columns.IsEmpty && result.Rows.IsEmpty))
            throw new InvalidDataException("查詢欄位數與計畫不符。");
        var visuals = result.Rows.IsEmpty
            ? ImmutableArray.Create(new VisualSpec("empty", "查詢資料（無資料列）", VisualKind.Table, null, null, null)) : plan.Visuals;
        var spec = new ReportSpec(1, result.ResultId, plan.Title, visuals); ReportSpecs.Validate(spec, result); return spec;
    }
    public static MeasureChange HtmlMeasure(string table, string name, QueryResult source, ReportSpec spec, TimeProvider? resultClock = null, ReportAppearance? appearance = null)
    {
        if (appearance?.Output == ReportOutputKind.HtmlFile) throw new InvalidDataException("HTML 度量值需使用報告度量值或 Dashboard 範本。");
        var html = HtmlReportRenderer.Render(source, spec, resultClock, appearance ?? TemplatePreferences.Default.For(ReportOutputKind.ReportMeasure));
        var expression = "\"" + html.Replace("\"", "\"\"") + "\"";
        if (expression.Length > 8000) throw new InvalidDataException("HTML 度量值超過 8000 字上限；請減少視覺或使用本機 HTML 匯出。");
        return new(table, name, expression); // Static query snapshot, not a filter-responsive DAX calculation.
    }
}

public sealed class DashboardCoordinator(ISemanticModelReader reader, IModelDefinitionWriter writer,
    IReadOnlyDaxQueryRunner runner, PermissionSession session, IModelWriteJournal? journal = null, TimeProvider? resultClock = null,
    QueryLimits? queryLimits = null, ModelModificationPolicy? modificationPolicy = null)
{
    private readonly QueryLimits resultLimits = queryLimits ?? new(100, 64_000, TimeSpan.FromSeconds(30));
    private readonly ModelWriteCoordinator writes = new(writer, session, journal, modificationPolicy);
    private readonly Dictionary<Guid, DashboardDraft> issued = [];
    public async Task<DashboardDraft> PrepareAsync(DashboardPlan plan, PermissionSnapshot permissions, CancellationToken token,
        ImmutableArray<MeasureChange>? extraChanges = null)
    {
        Check(permissions, Capability.MetadataRead);
        var model = await reader.ReadSnapshotAsync(token).WaitAsync(token); Check(permissions, Capability.MetadataRead);
        if (model.Identity != permissions.Target) throw new DefinitionConflictException();
        DashboardPlans.Validate(plan, model);
        var definition = await writes.PrepareAsync(plan.Metrics.AddRange(extraChanges ?? []), permissions, token);
        var query = DashboardPlans.BuildQuery(plan);
        var draft = new DashboardDraft(Guid.NewGuid(), plan, definition, query, DashboardPlans.Hash(plan, definition, query));
        lock (issued) { if (issued.Count >= 128) issued.Clear(); issued.Add(draft.Id, draft); }
        return draft;
    }
    private void ValidateDraft(DashboardDraft draft)
    {
        lock (issued) if (!issued.TryGetValue(draft.Id, out var original) || original != draft ||
            draft.Hash != DashboardPlans.Hash(draft.Plan, draft.Definition, draft.Query)) throw new UnauthorizedAccessException("Dashboard 預覽已變更。");
    }
    public ModelWriteApproval ApproveAfterUserConfirmation(DashboardDraft draft, PermissionSnapshot permissions)
    { ValidateDraft(draft); return writes.ApproveAfterUserConfirmation(draft.Definition, permissions); }
    public Task<WriteOutcome> ApplyAsync(DashboardDraft draft, ModelWriteApproval? approval, PermissionSnapshot permissions, CancellationToken token)
    { ValidateDraft(draft); return writes.ApplyAsync(draft.Definition, approval, permissions, token); }
    public async Task<QueryResult> QueryAsync(DashboardDraft draft, WriteOutcome outcome, PermissionSnapshot permissions, CancellationToken token)
    {
        ValidateDraft(draft); Check(permissions, Capability.DataQuery);
        if (!outcome.Success || outcome.After?.Identity != permissions.Target) throw new DefinitionConflictException();
        var current = await writer.ReadDefinitionAsync(token).WaitAsync(token); Check(permissions, Capability.DataQuery);
        if (current.Identity != permissions.Target || !DefinitionRules.MatchesApplied(draft.Definition.Plan, current)) throw new DefinitionConflictException();
        using var queryDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        queryDeadline.CancelAfter(resultLimits.EffectiveTimeout);
        var result = await runner.ExecuteAsync(permissions.Target, draft.Query, resultLimits, queryDeadline.Token).WaitAsync(queryDeadline.Token);
        Check(permissions, Capability.DataQuery);
        var after = await writer.ReadDefinitionAsync(token).WaitAsync(token); Check(permissions, Capability.DataQuery);
        if (after.Revision != current.Revision || result.Source != permissions.Target || result.Query != draft.Query) throw new DefinitionConflictException();
        QueryResultRetention.EnsureFresh(result, resultClock);
        if (result.Rows.IsDefault || result.Columns.IsDefault || result.Rows.Length > resultLimits.MaxRows ||
            result.Rows.Any(row => row.IsDefault || row.Length != result.Columns.Length) ||
            JsonSerializer.SerializeToUtf8Bytes(result).Length > resultLimits.MaxBytes)
            throw new InvalidDataException("Dashboard結果超過本機預算或結構限制。");
        DashboardPlans.Report(draft.Plan, result); return result;
    }
    private void Check(PermissionSnapshot permissions, Capability capability)
    { if (!session.IsCurrent(permissions) || !permissions.Allows(capability)) throw new UnauthorizedAccessException("Dashboard 授權失效。"); }
}
