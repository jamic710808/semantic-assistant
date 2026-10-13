using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticAssistant.Adapters.PowerBI.Cloud;

public sealed class CloudServiceException() : Exception("Power BI 雲端請求失敗或回應超過限制；請檢查租用戶／權限／服務限制。");
public sealed record CloudWorkspace(Guid Id, string Name);
public sealed record CloudDataset(Guid Id, string Name);
public sealed record WorkspacePage(ImmutableArray<CloudWorkspace> Items, int? NextOffset);

public sealed class PowerBiRestClient(IDelegatedTokenSource identity, Guid expectedTenant, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private const string Root = "https://api.powerbi.com/v1.0/myorg/";

    public async Task<WorkspacePage> ListWorkspacesAsync(int offset, CancellationToken token)
    {
        if (offset is < 0 or > 100_000) throw new ArgumentOutOfRangeException(nameof(offset));
        using var result = await SendAsync(HttpMethod.Get, $"groups?$top=201&$skip={offset}", null, 1_000_000, token);
        var items = result.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if (items.Length > 201) throw new CloudServiceException();
        return new(items.Take(200).Select(i => new CloudWorkspace(i.GetProperty("id").GetGuid(), i.GetProperty("name").GetString()!)).ToImmutableArray(),
            items.Length > 200 ? offset + 200 : null);
    }

    public async Task<ImmutableArray<CloudDataset>> ListDatasetsAsync(Guid workspace, CancellationToken token)
    {
        if (workspace == Guid.Empty) throw new ArgumentException("需要選取工作區。");
        using var result = await SendAsync(HttpMethod.Get, $"groups/{workspace}/datasets", null, 1_000_000, token);
        var items = result.RootElement.GetProperty("value").EnumerateArray().ToArray();
        if (items.Length > 1000) throw new CloudServiceException();
        return items.Select(i => new CloudDataset(i.GetProperty("id").GetGuid(), i.GetProperty("name").GetString()!)).ToImmutableArray();
    }

    internal async Task<JsonDocument> ExecuteDaxAsync(Guid workspace, Guid dataset, string dax, int maxBytes, CancellationToken token) =>
        await SendAsync(HttpMethod.Post, $"groups/{workspace}/datasets/{dataset}/executeQueries",
            new { queries = new[] { new { query = dax } }, serializerSettings = new { includeNulls = true } }, maxBytes, token);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string relative, object? body, int maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var credential = await identity.AcquireAsync(token);
        token.ThrowIfCancellationRequested();
        if (credential.Tenant != expectedTenant || credential.ExpiresAt <= DateTimeOffset.UtcNow) throw new CloudServiceException();
        using var request = new HttpRequestMessage(method, Root + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes) throw new CloudServiceException();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); if (count == 0) break;
            if (bytes.Length + count > maxBytes) throw new CloudServiceException();
            bytes.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        try { return JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 32 }); }
        catch (JsonException) { throw new CloudServiceException(); }
    }
    public void Dispose() => http.Dispose();
}
