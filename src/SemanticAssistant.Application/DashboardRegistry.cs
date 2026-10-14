using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed record ManagedDashboardMeasure(string Table, OwnedMeasure Definition);
public sealed record DashboardRecord(Guid Id, string SourceId, DashboardPlan Plan, MeasureChange HtmlMeasure,
    ReportAppearance Appearance, ImmutableArray<ManagedDashboardMeasure> Managed, DateTimeOffset CommittedAt)
{
    public string Revision => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, DashboardRegistry.JsonOptions)));
    public bool RecoveredFromIntent { get; init; }
}
public sealed record DashboardRegistration(Guid Id, DashboardPlan Plan, MeasureChange HtmlMeasure, ReportAppearance Appearance,
    DashboardRecord? Previous);
public sealed record DashboardCatalog(int Version, ImmutableArray<DashboardRecord> Entries);
public sealed record DashboardIntent(int Version, DashboardRegistration Registration, DefinitionPlan Plan, string PlanHash, DateTimeOffset PreparedAt);

// Host-local definitions only. Reopening Desktop is matched by complete owned-object
// lineage, never by a report name, port or database GUID alone.
public sealed class DashboardRegistry(string root)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        IgnoreReadOnlyProperties = true
    };
    private readonly string folder = Path.GetFullPath(root);
    private string PathName => Path.Combine(folder, "dashboards.json");
    private DashboardCatalog Load()
    {
        if (!File.Exists(PathName)) return new(1, []);
        var info = new FileInfo(PathName);
        if (info.Length > 4_500_000) throw new InvalidDataException("Dashboard登記超過上限。");
        var catalog = StrictJson.Parse<DashboardCatalog>(File.ReadAllText(PathName), 4_500_000, 24);
        if (catalog.Version != 1 || catalog.Entries.IsDefault || catalog.Entries.Length > 128 ||
            catalog.Entries.Select(e => e.Id).Distinct().Count() != catalog.Entries.Length) throw new InvalidDataException("Dashboard登記格式無效。");
        foreach (var entry in catalog.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.SourceId) || entry.SourceId.Length > 2048 ||
                entry.Plan is null || entry.HtmlMeasure is null || entry.Appearance is null || entry.Managed.IsDefaultOrEmpty || entry.Managed.Length > 16 ||
                entry.Managed.Any(m => m is null || m.Definition is null || m.Definition.Owner != DefinitionRules.Owner || m.Definition.OperationId is null || !m.Definition.IntegrityValid) ||
                JsonSerializer.SerializeToUtf8Bytes(entry.Plan, JsonOptions).Length > 32_000) throw new InvalidDataException("Dashboard登記項目無效。");
            ReportTemplates.Resolve(entry.Appearance);
        }
        return catalog;
    }
    public ImmutableArray<DashboardRecord> List(DefinitionState state)
    {
        var stored = Load().Entries.Where(e => e.SourceId == state.Identity.Id || e.Managed.Any(m => SameStamp(m, Find(state, m.Table, m.Definition.Name)))).ToList();
        var recovered = Recoverable(state).Where(r => !stored.Any(e => e.Id == r.Id && Matches(e, state))).ToArray();
        return stored.Where(e => !recovered.Any(r => r.Id == e.Id)).Concat(recovered).Take(128).ToImmutableArray();
    }
    private ImmutableArray<DashboardRecord> Recoverable(DefinitionState state)
    {
        if (!Directory.Exists(folder)) return [];
        var entries = ImmutableArray.CreateBuilder<DashboardRecord>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("intent-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(256))
        {
            if (file.Length > 160_000) continue;
            try
            {
                var intent = StrictJson.Parse<DashboardIntent>(File.ReadAllText(file.FullName), 160_000, 24);
                if (intent.Version != 1 || intent.Plan.OperationId == Guid.Empty || intent.PlanHash != DefinitionRules.HashPlan(intent.Plan) ||
                    intent.Plan.Changes.IsDefaultOrEmpty || !intent.Plan.Columns.IsEmpty || !intent.Plan.Relationships.IsEmpty ||
                    intent.Registration.Id == Guid.Empty || entries.Any(e => e.Id == intent.Registration.Id)) continue;
                var registration = intent.Registration;
                if (intent.Plan.Changes.Length != registration.Plan.Metrics.Length + 1 ||
                    !registration.Plan.Metrics.Append(registration.HtmlMeasure).All(c => intent.Plan.Changes.Contains(c))) continue;
                if (!intent.Plan.Changes.All(c => Find(state, c.Table, c.Name) is { } m && m.IntegrityValid &&
                    m.Owner == DefinitionRules.Owner && m.Expression == c.Expression && m.OperationId == intent.Plan.OperationId)) continue;
                var retained = registration.Previous?.Managed.Where(m => !intent.Plan.Changes.Any(c => c.Table.Equals(m.Table, StringComparison.OrdinalIgnoreCase) &&
                    c.Name.Equals(m.Definition.Name, StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
                if (!retained.All(m => SameStamp(m, Find(state, m.Table, m.Definition.Name)))) continue;
                var objects = retained.Concat(intent.Plan.Changes.Select(c => new ManagedDashboardMeasure(c.Table, Find(state, c.Table, c.Name)!))).ToImmutableArray();
                if (objects.Length > 16) continue;
                var recovered = new DashboardRecord(registration.Id, state.Identity.Id, registration.Plan, registration.HtmlMeasure,
                    registration.Appearance, objects, intent.PreparedAt) { RecoveredFromIntent = true };
                if (Matches(recovered, state)) entries.Add(recovered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NullReferenceException)
            { /* An incomplete/unknown intent never becomes authority to modify the model. */ }
        }
        return entries.ToImmutable();
    }
    public DashboardRecord Get(Guid id, DefinitionState state) => List(state).SingleOrDefault(e => e.Id == id)
        ?? throw new ToolInputException("目前模型沒有這個Dashboard登記。");
    public static OwnedMeasure? Find(DefinitionState state, string table, string name) => state.Tables.SingleOrDefault(t =>
        t.Name.Equals(table, StringComparison.OrdinalIgnoreCase))?.Measures.SingleOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static bool SameStamp(ManagedDashboardMeasure expected, OwnedMeasure? actual) => actual is not null && actual.IntegrityValid &&
        actual.Owner == DefinitionRules.Owner && actual.Name == expected.Definition.Name && actual.Expression == expected.Definition.Expression &&
        actual.OperationId == expected.Definition.OperationId && actual.IntegrityStamp == expected.Definition.IntegrityStamp;
    public static bool Matches(DashboardRecord entry, DefinitionState state) => entry.Managed.All(m => SameStamp(m, Find(state, m.Table, m.Definition.Name))) &&
        entry.Plan.Metrics.Append(entry.HtmlMeasure).All(c => entry.Managed.Any(m => m.Table.Equals(c.Table, StringComparison.OrdinalIgnoreCase) &&
            m.Definition.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && m.Definition.Expression == c.Expression));
    public bool IsManaged(DefinitionState state, string table, string name) => List(state).Any(e => e.Managed.Any(m =>
        m.Table.Equals(table, StringComparison.OrdinalIgnoreCase) && m.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public RegistryLease Begin(DashboardRegistration registration, DefinitionPreview preview)
    {
        Directory.CreateDirectory(folder);
        var fileLock = new FileStream(Path.Combine(folder, "dashboards.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var catalog = Load();
            if (registration.Id == Guid.Empty || registration.Previous is { } prior && prior.Id != registration.Id ||
                preview.Plan.Changes.Length != registration.Plan.Metrics.Length + 1 ||
                !registration.Plan.Metrics.Append(registration.HtmlMeasure).All(c => preview.Plan.Changes.Contains(c))) throw new DefinitionConflictException();
            if (registration.Previous is { } previous)
            {
                var current = catalog.Entries.SingleOrDefault(e => e.Id == previous.Id);
                if (current?.Revision != previous.Revision && (current is not null && Matches(current, preview.Before) ||
                    !Recoverable(preview.Before).Any(e => e.Id == previous.Id && e.Revision == previous.Revision)) ||
                    !Matches(previous, preview.Before)) throw new DefinitionConflictException();
            }
            else if (List(preview.Before).Length >= 128 || catalog.Entries.Any(e => e.Id == registration.Id)) throw new DefinitionConflictException();
            if (catalog.Entries.Where(e => e.Id != registration.Id && (e.SourceId == preview.Before.Identity.Id || Matches(e, preview.Before)))
                .Any(e => e.Managed.Any(m => preview.Plan.Changes.Any(c => c.Table.Equals(m.Table, StringComparison.OrdinalIgnoreCase) &&
                    c.Name.Equals(m.Definition.Name, StringComparison.OrdinalIgnoreCase))))) throw new DefinitionConflictException();
            // Preserve a recoverable intent before touching the model. Not a deployed record.
            var intent = JsonSerializer.SerializeToUtf8Bytes(new DashboardIntent(1, registration, preview.Plan, preview.PlanHash, DateTimeOffset.UtcNow), JsonOptions);
            if (intent.Length > 160_000) throw new InvalidDataException("Dashboard部署意圖超過限制。");
            File.WriteAllBytes(Path.Combine(folder, "intent-" + preview.Plan.OperationId.ToString("N") + ".json"), intent);
            return new(this, fileLock, catalog, registration, preview);
        }
        catch { fileLock.Dispose(); throw; }
    }
    public sealed class RegistryLease : IDisposable
    {
        private readonly DashboardRegistry store;
        private readonly FileStream fileLock;
        private readonly DashboardCatalog before;
        private readonly DashboardRegistration registration;
        private readonly DefinitionPreview preview;
        internal RegistryLease(DashboardRegistry store, FileStream fileLock, DashboardCatalog before, DashboardRegistration registration, DefinitionPreview preview)
        { this.store = store; this.fileLock = fileLock; this.before = before; this.registration = registration; this.preview = preview; }
        public DashboardRecord Commit(WriteOutcome outcome)
        {
            if (!outcome.Success || outcome.After is not { } after || !DefinitionRules.MatchesApplied(preview.Plan, after)) throw new DefinitionConflictException();
            var objects = (registration.Previous?.Managed.Select(m => (m.Table, m.Definition.Name)) ?? [])
                .Concat(preview.Plan.Changes.Select(c => (c.Table, c.Name)))
                .GroupBy(key => key.Table + "\0" + key.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).Select(key =>
                    new ManagedDashboardMeasure(key.Table, Find(after, key.Table, key.Name) ?? throw new DefinitionConflictException())).ToImmutableArray();
            if (objects.Length > 16) throw new InvalidDataException("Dashboard管理物件超過16個。");
            var record = new DashboardRecord(registration.Id, after.Identity.Id, registration.Plan, registration.HtmlMeasure, registration.Appearance, objects, DateTimeOffset.UtcNow);
            if (!Matches(record, after)) throw new DefinitionConflictException();
            var updated = new DashboardCatalog(1, before.Entries.Where(e => e.Id != record.Id).Append(record).ToImmutableArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
            if (bytes.Length > 4_500_000) throw new InvalidDataException("Dashboard登記超過上限。");
            var temporary = Path.Combine(store.folder, "dashboards-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(store.PathName)) File.Copy(store.PathName, Path.Combine(store.folder, "backup-" + Guid.NewGuid().ToString("N") + ".json"));
                File.Move(temporary, store.PathName, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return record;
        }
        public void Dispose() => fileLock.Dispose();
    }
}
