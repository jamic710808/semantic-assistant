using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Adapters.PowerBI.Local;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.Adapters.PowerBI.Cloud;

public sealed class CloudModelAdapter : ISemanticModelReader, IReadOnlyDaxQueryRunner, IDisposable
{
    private readonly IDelegatedTokenSource identity;
    private readonly PowerBiRestClient rest;
    private readonly Guid tenant;
    private readonly CloudWorkspace workspace;
    private readonly CloudDataset dataset;
    public string XmlaEndpoint { get; }

    public IModelDefinitionWriter CreateWriter() => new TomDefinitionWriter(
        token => OpenWriterAsync(false, false, token), token => OpenWriterAsync(true, true, token), token => OpenWriterAsync(true, false, token));

    private async Task<TomWriterConnection> OpenWriterAsync(bool write, bool allowInteractive, CancellationToken token)
    {
        var access = write ? await (identity as IModelWriteTokenSource ?? throw new CloudServiceException()).AcquireWriteAsync(allowInteractive, token)
            : await identity.AcquireAsync(token);
        if (access.Tenant != tenant || access.ExpiresAt <= DateTimeOffset.UtcNow) throw new CloudServiceException();
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); var server = new Server();
            try
            {
                server.AccessToken = new(access.Value, access.ExpiresAt, null);
                var connection = new DbConnectionStringBuilder { ["Data Source"] = XmlaEndpoint, ["Connect Timeout"] = 5 };
                server.Connect(connection.ConnectionString);
                var db = server.Databases.Cast<Database>().SingleOrDefault(d => Guid.TryParse(d.ID, out var id) && id == dataset.Id) ?? throw new CloudServiceException();
                token.ThrowIfCancellationRequested();
                return new TomWriterConnection(server, db, $"cloud:{tenant}:{workspace.Id}:{dataset.Id}", () =>
                { if (access.ExpiresAt <= DateTimeOffset.UtcNow) throw new CloudServiceException(); });
            }
            catch { server.Dispose(); throw; }
        }, token);
    }

    public CloudModelAdapter(IDelegatedTokenSource identity, Guid tenant, CloudWorkspace workspace,
        CloudDataset dataset, HttpMessageHandler? restHandler = null)
    {
        if (tenant == Guid.Empty || workspace.Id == Guid.Empty || dataset.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(workspace.Name) || workspace.Name.Length > 256)
            throw new ArgumentException("需要明確選取租用戶、工作區與資料集。");
        this.identity = identity; this.tenant = tenant; this.workspace = workspace; this.dataset = dataset;
        rest = new(identity, tenant, restHandler);
        XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/" + Uri.EscapeDataString(workspace.Name);
    }

    public async Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var access = await identity.AcquireAsync(token);
        if (access.Tenant != tenant || access.ExpiresAt <= DateTimeOffset.UtcNow) throw new CloudServiceException();
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var server = new Server();
            server.AccessToken = new(access.Value, access.ExpiresAt, null);
            var connection = new DbConnectionStringBuilder { ["Data Source"] = XmlaEndpoint, ["Connect Timeout"] = 5 };
            server.Connect(connection.ConnectionString);
            var db = server.Databases.Cast<Database>().SingleOrDefault(d => Guid.TryParse(d.ID, out var id) && id == dataset.Id)
                ?? throw new CloudServiceException(); // Never infer another catalog from a similar display name.
            var tables = db.Model.Tables.Select(t => new TableDefinition(t.Name,
                t.Columns.Where(c => c is not RowNumberColumn).Select(c => new ColumnDefinition(c.Name, c.DataType.ToString(), c is CalculatedColumn cc ? cc.Expression : null)).ToImmutableArray(),
                t.Measures.Select(m => new MeasureDefinition(m.Name, m.Expression ?? "")).ToImmutableArray())).ToImmutableArray();
            var relationships = db.Model.Relationships.OfType<SingleColumnRelationship>().Select(r => new RelationshipDefinition(
                r.Name, r.FromColumn.Table.Name, r.FromColumn.Name, r.ToColumn.Table.Name, r.ToColumn.Name, r.IsActive,
                r.FromCardinality.ToString(), r.ToCardinality.ToString(), r.CrossFilteringBehavior.ToString(), r.SecurityFilteringBehavior.ToString())).ToImmutableArray();
            token.ThrowIfCancellationRequested();
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new { tables, relationships });
            if (metadata.Length > 2_000_000) throw new CloudServiceException();
            var revision = Convert.ToHexString(SHA256.HashData(metadata)).ToLowerInvariant();
            return new ModelSnapshot(new($"cloud:{tenant}:{workspace.Id}:{dataset.Id}", db.Name, revision, false), tables)
            { Relationships = relationships };
        }, token);
    }

    public async Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token)
    {
        ReadOnlyDax.Validate(dax);
        if (limits.MaxRows is < 1 or > 100 || limits.MaxBytes is < 1024 or > 64_000 || limits.EffectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(limits.EffectiveTimeout);
        if ((await ReadSnapshotAsync(timeout.Token)).Identity != target) throw new CloudServiceException();
        using var response = await rest.ExecuteDaxAsync(workspace.Id, dataset.Id, dax, limits.MaxBytes, timeout.Token);
        var result = ParseQueryResponse(response.RootElement, target, dax, limits);
        if ((await ReadSnapshotAsync(timeout.Token)).Identity != target) throw new CloudServiceException();
        return result;
    }

    public static QueryResult ParseQueryResponse(JsonElement root, ModelIdentity target, string dax, QueryLimits limits)
    {
        if (limits.MaxRows is < 1 or > 100 || limits.MaxBytes is < 1024 or > 64_000) throw new ArgumentOutOfRangeException(nameof(limits));
        try
        {
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("results", out var results) || results.GetArrayLength() != 1)
                throw new CloudServiceException();
            var result = results[0];
            if (result.TryGetProperty("error", out _) || !result.TryGetProperty("tables", out var tables) || tables.GetArrayLength() != 1)
                throw new CloudServiceException();
            var table = tables[0]; if (table.TryGetProperty("error", out _)) throw new CloudServiceException();
            var data = table.GetProperty("rows").EnumerateArray().ToArray();
            var columns = data.SelectMany(r => r.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal).Take(65).ToImmutableArray();
            if (columns.Length > 64 || (columns.IsEmpty && data.Length > 0)) throw new CloudServiceException();
            var rows = ImmutableArray.CreateBuilder<ImmutableArray<JsonElement>>();
            var id = Guid.NewGuid().ToString("N"); bool truncated = data.Length > limits.MaxRows;
            foreach (var source in data.Take(limits.MaxRows))
            {
                var row = columns.Select(c => source.TryGetProperty(c, out var value) ? value.Clone() : ContractJson.Element<object?>(null)).ToImmutableArray();
                if (row.Any(v => v.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined)) throw new CloudServiceException();
                rows.Add(row);
                if (JsonSerializer.SerializeToUtf8Bytes(new QueryResult(id, target, columns, rows.ToImmutable(), true, dax)).Length > limits.MaxBytes)
                { rows.RemoveAt(rows.Count - 1); truncated = true; break; }
            }
            return new(id, target, columns, rows.ToImmutable(), truncated, dax);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new CloudServiceException(); }
    }
    public void Dispose() => rest.Dispose();
}
