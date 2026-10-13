using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.AnalysisServices.Tabular;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace SemanticAssistant.Adapters.PowerBI.Local;

public sealed record LocalCatalog(string Id, string Name);

public sealed class DesktopModelAdapter(DesktopInstance instance, string catalogId, DesktopDiscovery discovery)
    : ISemanticModelReader, IReadOnlyDaxQueryRunner
{
    private static string Connection(DesktopInstance instance, string? catalog = null)
    {
        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = "localhost:" + instance.Owner.Port,
            ["Connect Timeout"] = 3
        };
        if (catalog is not null) builder["Initial Catalog"] = catalog;
        return builder.ConnectionString;
    }

    public static Task<ImmutableArray<LocalCatalog>> ListCatalogsAsync(DesktopInstance instance, DesktopDiscovery discovery,
        CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); discovery.EnsureCurrent(instance);
        using var server = new Server(); server.Connect(Connection(instance));
        var catalogs = server.Databases.Cast<Database>().Select(d => new LocalCatalog(d.ID, d.Name)).Take(65).ToImmutableArray();
        if (catalogs.Length > 64) throw new InvalidDataException("語意模型數量超限。");
        token.ThrowIfCancellationRequested(); discovery.EnsureCurrent(instance);
        return catalogs;
    }, token);

    public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    public IModelDefinitionWriter CreateWriter() => new TomDefinitionWriter(token => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); discovery.EnsureCurrent(instance);
        var server = new Server();
        try
        {
            server.Connect(Connection(instance));
            var database = server.Databases.Cast<Database>().SingleOrDefault(d => d.ID == catalogId) ?? throw new IOException();
            discovery.EnsureCurrent(instance); token.ThrowIfCancellationRequested();
            var id = $"desktop:{instance.Owner.ProcessId}:{instance.Owner.StartedUtcTicks}:{instance.Owner.Port}:{catalogId}";
            return new TomWriterConnection(server, database, id, () => discovery.EnsureCurrent(instance));
        }
        catch { server.Dispose(); throw; }
    }, token));

    private ModelSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); discovery.EnsureCurrent(instance);
        token.ThrowIfCancellationRequested();
        using var server = new Server(); server.Connect(Connection(instance));
        var database = server.Databases.Cast<Database>().SingleOrDefault(d => d.ID == catalogId) ?? throw new IOException("語意模型已關閉。");
        var tables = database.Model.Tables.Select(t => new TableDefinition(t.Name,
            t.Columns.Where(c => c is not RowNumberColumn).Select(c => new ColumnDefinition(c.Name, c.DataType.ToString(), c is CalculatedColumn cc ? cc.Expression : null)).ToImmutableArray(),
            t.Measures.Select(m => new MeasureDefinition(m.Name, m.Expression ?? "")).ToImmutableArray())).ToImmutableArray();
        var relationships = database.Model.Relationships.OfType<SingleColumnRelationship>().Select(r => new RelationshipDefinition(
            r.Name, r.FromColumn.Table.Name, r.FromColumn.Name, r.ToColumn.Table.Name, r.ToColumn.Name, r.IsActive,
            r.FromCardinality.ToString(), r.ToCardinality.ToString(), r.CrossFilteringBehavior.ToString(), r.SecurityFilteringBehavior.ToString())).ToImmutableArray();
        token.ThrowIfCancellationRequested(); discovery.EnsureCurrent(instance);
        token.ThrowIfCancellationRequested();
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { tables, relationships }))).ToLowerInvariant();
        var id = $"desktop:{instance.Owner.ProcessId}:{instance.Owner.StartedUtcTicks}:{instance.Owner.Port}:{catalogId}";
        return new(new(id, database.Name, revision, false), tables) { Relationships = relationships };
    }

    public async Task<QueryResult> ExecuteAsync(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token)
    {
        ReadOnlyDax.Validate(dax);
        if (limits.MaxRows is < 1 or > 100 || limits.MaxBytes is < 1024 or > 64_000 || limits.EffectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limits.EffectiveTimeout);
        try
        {
            var result = await Task.Run(() => Execute(target, dax, limits, deadline.Token), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        { throw new OperationCanceledException(deadline.Token); }
    }

    private QueryResult Execute(ModelIdentity target, string dax, QueryLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Read(token).Identity != target) throw new IOException("模型結構版本已改變，請重新讀取並授權。");
        using var connection = new AdomdConnection(Connection(instance, catalogId));
        connection.Open();
        token.ThrowIfCancellationRequested(); discovery.EnsureCurrent(instance);
        token.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = dax;
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(limits.EffectiveTimeout.TotalSeconds));
        using var cancel = token.Register(() => { try { command.Cancel(); } catch { /* Cancel is best effort; no results are accepted after cancellation. */ } });
        token.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult);
        if (reader.FieldCount is < 1 or > 64) throw new InvalidDataException("查詢欄位數超限。");
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToImmutableArray();
        var buffer = new QueryResultBuffer(target, columns, dax, limits);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (!buffer.TryAdd(() =>
            {
                var row = ImmutableArray.CreateBuilder<JsonElement>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    if (value is string text && text.Length > limits.MaxBytes) throw new InvalidDataException("單值超過大小上限。");
                    row.Add(value is DBNull ? ContractJson.Element<object?>(null) : JsonSerializer.SerializeToElement(value, value.GetType()));
                }
                return row.ToImmutable();
            })) break;
        }
        token.ThrowIfCancellationRequested();
        if (Read(token).Identity != target) throw new IOException("查詢期間模型結構已改變，結果已丟棄。");
        return buffer.Build();
    }
}

public static class ReadOnlyDax
{
    // Statement envelope guard, not a full DAX parser. The service performs syntax validation.
    public static void Validate(string dax)
    {
        if (string.IsNullOrWhiteSpace(dax) || dax.Length > 8000) throw new ToolInputException("DAX 必須為 1–8000 個字元。");
        var text = dax.TrimStart();
        bool Starts(string word) => dax.TrimStart().StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
            (text.Length == word.Length || char.IsWhiteSpace(text[word.Length]));
        if (!(Starts("EVALUATE") || Starts("DEFINE"))) throw new ToolInputException("只接受 EVALUATE 或 DEFINE 開頭的 DAX 查詢。");
        // XMLA/MDX management commands cannot enter through a read-only statement envelope.
        if (dax.Contains('\0')) throw new ToolInputException("DAX 含無效控制字元。");
    }
}
