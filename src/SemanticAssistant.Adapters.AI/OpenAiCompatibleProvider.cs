using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.AI;

public interface ICredentialSource { string? ReadSecret(ProviderIdentity provider); }

public sealed class OpenAiCompatibleProvider : IAiProvider, IDisposable
{
    private readonly HttpClient client;
    private readonly ICredentialSource credentials;
    private readonly TimeProvider resultClock;
    public bool UsesExternalNetwork => true; // Loopback also requires explicit egress consent.
    public ProviderIdentity Identity { get; }

    public OpenAiCompatibleProvider(ProviderIdentity identity, ICredentialSource credentials,
        HttpMessageHandler? handler = null, TimeProvider? resultClock = null)
    {
        this.resultClock = resultClock ?? TimeProvider.System;
        Identity = ValidateIdentity(identity);
        this.credentials = credentials;
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static ProviderIdentity ValidateIdentity(ProviderIdentity identity)
        => ProviderProfiles.Validate(identity);

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var authorization = request.Authorization ?? throw new InvalidOperationException("缺少外送授權。");
        authorization.EnsureValid(Identity);
        var payload = BuildPayload(request);
        if (Encoding.UTF8.GetByteCount(payload) > 512_000) throw new ProviderProtocolException();
        using var http = new HttpRequestMessage(HttpMethod.Post, Identity.Endpoint);
        http.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var secret = credentials.ReadSecret(Identity);
        if (!string.IsNullOrEmpty(secret)) http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        http.Headers.Accept.Add(new("text/event-stream"));
        authorization.EnsureValid(Identity);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureResultsFresh(request);
        using var response = await SendAsync(http, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AiProviderFailureException((int)response.StatusCode switch
            {
                400 or 422 => AiProviderFailure.InvalidRequest,
                401 => AiProviderFailure.Authentication,
                403 => AiProviderFailure.AccessDenied,
                404 => AiProviderFailure.NotFound,
                429 => AiProviderFailure.RateLimited,
                >= 500 and <= 599 => AiProviderFailure.ServiceUnavailable,
                _ => AiProviderFailure.UnexpectedHttpStatus
            }); // Never read or expose remote error bodies, headers or secrets.
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new ProviderProtocolException();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in ChatStreamParser.ParseAsync(stream, cancellationToken))
        {
            authorization.EnsureValid(Identity);
            EnsureResultsFresh(request);
            yield return item;
        }
    }

    private string BuildPayload(ProviderRequest request)
    {
        var auth = request.Authorization!;
        ConversationContextPolicy.EnsureValid(request.History, auth, resultClock);
        if (request.Tools.Any(t => !auth.Allows(ToolPolicyEngine.RequiredEgress(t))))
            throw new EgressAuthorizationException();
        var messages = new List<object>
        {
            new { role = "system", content = ConversationInstructions.For(request.Intent) },
        };
        if (auth.Allows(EgressCategory.Metadata)) messages.Add(new { role = "system", content = request.Context });
        foreach (var previous in request.History)
            messages.Add(new { role = previous.Role, content = previous.Content });
        messages.Add(new { role = "user", content = request.UserMessage });
        foreach (var round in request.Messages.GroupBy(m => m.Round))
        {
            var calls = round.Where(m => m.Call is not null).Select(m => m.Call!).ToArray();
            foreach (var call in calls) ToolPolicyEngine.EnsureCallEgress(call, auth);
            if (calls.Length > 0)
            {
                messages.Add(new
                {
                    role = "assistant",
                    tool_calls = calls.Select(call => new
                    {
                        id = call.Id,
                        type = "function",
                        function = new { name = call.Name, arguments = call.Arguments.GetRawText() }
                    }).ToArray()
                });
            }
            foreach (var message in round)
            {
                if (message.Result is { } result)
                {
                    ToolPolicyEngine.EnsureResultFresh(result, resultClock);
                    ToolPolicyEngine.EnsureResultEgress(result, auth);
                    messages.Add(new { role = "tool", tool_call_id = result.CallId, content = JsonSerializer.Serialize(result) });
                }
                else if (message.Call is not null) { }
                else if (message.Role == "assistant") messages.Add(new { role = "assistant", content = message.Content });
                else throw new ProviderProtocolException();
            }
        }
        var body = new Dictionary<string, object> { ["model"] = Identity.Model, ["stream"] = true, ["messages"] = messages };
        // GPT-6 Luna supports Chat Completions function calls only without reasoning.
        // Scope this compatibility setting to the official endpoint and exact model.
        if (Identity.Endpoint == "https://api.openai.com/v1/chat/completions" && Identity.Model == "gpt-6-luna")
            body["reasoning_effort"] = "none";
        if (!request.Tools.IsEmpty)
            body["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters
                }
            }).ToArray();
        return JsonSerializer.Serialize(body);
    }

    public void Dispose() => client.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new AiProviderFailureException(AiProviderFailure.Network); }
    }

    private void EnsureResultsFresh(ProviderRequest request)
    {
        ConversationContextPolicy.EnsureValid(request.History, request.Authorization, resultClock);
        foreach (var message in request.Messages)
            if (message.Result is { } result) ToolPolicyEngine.EnsureResultFresh(result, resultClock);
    }
}
