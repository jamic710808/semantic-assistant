using System.Net;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Adapters.PowerBI.Cloud;
using SemanticAssistant.Application;
using Xunit;

namespace SemanticAssistant.Application.Tests;

public sealed class CloudAdapterTests
{
    private static readonly Guid Tenant = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private sealed class TokenSource(Guid tenant, bool expired = false) : IDelegatedTokenSource
    {
        public Task<DelegatedToken> AcquireAsync(CancellationToken token) =>
            Task.FromResult(new DelegatedToken("synthetic-token-only", DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5), tenant));
    }
    private sealed class CancellingTokenSource(CancellationTokenSource cancellation) : IDelegatedTokenSource
    {
        public int Reads;
        public Task<DelegatedToken> AcquireAsync(CancellationToken token)
        {
            Reads++; cancellation.Cancel();
            return Task.FromResult(new DelegatedToken("synthetic-token-only", DateTimeOffset.UtcNow.AddMinutes(5), Tenant));
        }
    }
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        public string? Uri, Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri!.AbsoluteUri; Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task WorkspaceSelectionUsesFixedEndpointAndBearerOnly()
    {
        var handler = new Handler("{\"value\":[{\"id\":\"00000002-0000-0000-0000-000000000001\",\"name\":\"合成工作區\"}]}");
        using var api = new PowerBiRestClient(new TokenSource(Tenant), Tenant, handler);
        var page = await api.ListWorkspacesAsync(0, default);
        Assert.Equal("合成工作區", Assert.Single(page.Items).Name); Assert.Null(page.NextOffset);
        Assert.StartsWith("https://api.powerbi.com/v1.0/myorg/groups?", handler.Uri);
        Assert.Equal("Bearer synthetic-token-only", handler.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongTenantOrExpiredTokenStopsBeforeNetwork(bool expired)
    {
        var handler = new Handler("{}");
        using var api = new PowerBiRestClient(new TokenSource(expired ? Tenant : Guid.NewGuid(), expired), Tenant, handler);
        await Assert.ThrowsAsync<CloudServiceException>(() => api.ListWorkspacesAsync(0, default)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PreCancelledRestCallDoesNotAcquireCredentialOrSendHttp()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var identity = new CancellingTokenSource(cancellation); var handler = new Handler("{}");
        using var api = new PowerBiRestClient(identity, Tenant, handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ListWorkspacesAsync(0, cancellation.Token));
        Assert.Equal(0, identity.Reads); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringCredentialAcquisitionStopsBeforeHttp()
    {
        using var cancellation = new CancellationTokenSource();
        var identity = new CancellingTokenSource(cancellation); var handler = new Handler("{}");
        using var api = new PowerBiRestClient(identity, Tenant, handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ListWorkspacesAsync(0, cancellation.Token));
        Assert.Equal(1, identity.Reads); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void TokenCannotLeakViaToStringOrJson()
    {
        var token = new DelegatedToken("synthetic-sensitive-token", DateTimeOffset.UtcNow, Tenant);
        Assert.DoesNotContain("synthetic-sensitive", token.ToString());
        Assert.DoesNotContain("synthetic-sensitive", JsonSerializer.Serialize(token));
    }

    [Fact]
    public async Task RedirectAndRemoteErrorsExposeNoBody()
    {
        var handler = new Handler("synthetic-remote-private-error", HttpStatusCode.TemporaryRedirect);
        using var api = new PowerBiRestClient(new TokenSource(Tenant), Tenant, handler);
        var error = await Assert.ThrowsAsync<CloudServiceException>(() => api.ListWorkspacesAsync(0, default));
        Assert.DoesNotContain("synthetic-remote-private-error", error.Message); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void RestQueryRowsPreserveNullsTruncationAndSource()
    {
        using var json = JsonDocument.Parse("{\"results\":[{\"tables\":[{\"rows\":[{\"[a]\":1,\"[b]\":null},{\"[a]\":2}]}]}]}");
        var target = DemoModel.Snapshot.Identity;
        var result = CloudModelAdapter.ParseQueryResponse(json.RootElement, target, "EVALUATE ROW(\"a\",1)", new(MaxRows: 1));
        Assert.Equal(target, result.Source); Assert.Equal(2, result.Columns.Length); Assert.Single(result.Rows); Assert.True(result.Truncated);
        Assert.Equal(JsonValueKind.Null, result.Rows[0][1].ValueKind);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"private\"}}")]
    [InlineData("{\"results\":[{\"error\":{}}]}")]
    [InlineData("{\"results\":[{\"tables\":[],\"error\":{}}]}")]
    [InlineData("{\"results\":[{\"tables\":[{\"rows\":[{\"x\":{\"nested\":1}}]}]}]}")]
    [InlineData("{\"results\":[]}")]
    public void InvalidOrMultipleResultsAreRejected(string text)
    {
        using var json = JsonDocument.Parse(text);
        Assert.Throws<CloudServiceException>(() => CloudModelAdapter.ParseQueryResponse(json.RootElement, DemoModel.Snapshot.Identity, "EVALUATE x", new()));
    }

    [Fact]
    public void XmlaEndpointCannotInjectUrlSyntax()
    {
        using var adapter = new CloudModelAdapter(new TokenSource(Tenant), Tenant, new(Guid.NewGuid(), "a?b#c/d"), new(Guid.NewGuid(), "synthetic"));
        Assert.EndsWith("a%3Fb%23c%2Fd", adapter.XmlaEndpoint);
    }

    [Fact]
    public void EmptyCloudResultDoesNotInventColumnNamesOrValues()
    {
        using var json = JsonDocument.Parse("{\"results\":[{\"tables\":[{\"rows\":[]}]}]}");
        var result = CloudModelAdapter.ParseQueryResponse(json.RootElement, DemoModel.Snapshot.Identity, "EVALUATE x", new());
        Assert.Empty(result.Columns); Assert.Empty(result.Rows);
        var html = HtmlReportRenderer.Render(result);
        Assert.Contains("查詢未回傳資料列", html); Assert.Contains("0.00", html);
    }
}
