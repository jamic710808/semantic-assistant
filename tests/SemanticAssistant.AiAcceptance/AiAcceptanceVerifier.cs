using System.Collections.Immutable;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticAssistant.Adapters.AI;
using SemanticAssistant.Adapters.Demo;
using SemanticAssistant.Application;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.AiAcceptance;

public sealed record AiAcceptanceProfile(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] ProviderIdentity Provider,
    [property: JsonRequired] int TimeoutSeconds)
{
    public static AiAcceptanceProfile Parse(string json)
    {
        if (json.StartsWith('\uFEFF')) json = json[1..];
        var profile = StrictJson.Parse<AiAcceptanceProfile>(json, 8192, 5);
        if (profile.FormatVersion != 1 || profile.Provider is null || profile.TimeoutSeconds is < 5 or > 120)
            throw new InvalidDataException();
        return profile with { Provider = ProviderProfiles.Validate(profile.Provider) };
    }
}

public sealed record AiAcceptanceGate(string Id, string Status, string Code, int HttpRequests,
    int CredentialReads, int ModelReads, int Queries);
public sealed record AiAcceptanceReport(int FormatVersion, DateTimeOffset VerifiedUtc, string ProfileSha256,
    bool LiveRequested, string Transport, bool SyntheticOnly, bool LocalPreflightPassed,
    bool ConfiguredEndpointVerified, bool GeneralModelQualityVerified,
    ImmutableArray<AiAcceptanceGate> Gates);

// A dedicated verifier always owns its DemoModel. It accepts no PBIX, source identity,
// user prompt, private query or arbitrary model adapter from the command line.
public sealed class AiAcceptanceVerifier(AiAcceptanceProfile profile, ICredentialSource credentials,
    HttpMessageHandler? handler = null)
{
    public async Task<AiAcceptanceReport> RunAsync(bool live = false, bool allowMessage = false,
        bool allowMetadata = false, bool allowQueryResults = false, CancellationToken token = default)
    {
        profile = AiAcceptanceProfile.Parse(JsonSerializer.Serialize(profile));
        if (live && !(allowMessage && allowMetadata && allowQueryResults))
            throw new UnauthorizedAccessException();
        var source = new ObservedCredentials(credentials);
        using var transport = new ObservedHttp(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        using var provider = new OpenAiCompatibleProvider(profile.Provider, source, transport);
        var gates = ImmutableArray.CreateBuilder<AiAcceptanceGate>();
        foreach (var kind in new[] { "no-consent", "expired-consent", "wrong-provider", "revoked-consent" })
        {
            token.ThrowIfCancellationRequested();
            var model = new ObservedModel();
            var consent = Consent(EgressCategory.UserMessage);
            if (kind == "expired-consent") consent = consent with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            if (kind == "wrong-provider") consent = consent with { Provider = profile.Provider with { Id = "different-provider" } };
            var session = Session(kind == "no-consent" ? null : consent);
            var pinned = session.Snapshot;
            if (kind == "revoked-consent") session.Update(pinned.Target, pinned.Mode, pinned.Granted);
            var outcome = await RunTurn(provider, model, session, pinned, "合成驗收", _ => Task.CompletedTask, token);
            var expected = kind == "no-consent" ? "external_provider_disabled" : "egress_denied";
            gates.Add(new(kind, outcome.Code == expected && transport.Requests == 0 && source.Reads == 0 &&
                model.Reads == 0 && model.Queries == 0 ? "passed" : "failed", SafeCode(outcome.Code),
                transport.Requests, source.Reads, model.Reads, model.Queries));
        }
        if (gates.Any(g => g.Status != "passed")) return Report();
        if (!live)
        {
            foreach (var name in new[] { "message-only", "tool-roundtrip", "cancel-on-text", "revoke-on-text" })
                gates.Add(new(name, "not-run", "live-consent-required", 0, 0, 0, 0));
            return Report();
        }
        var previousStageFailed = false;
        foreach (var kind in new[] { "message-only", "tool-roundtrip", "cancel-on-text", "revoke-on-text" })
        {
            if (token.IsCancellationRequested || previousStageFailed)
            {
                gates.Add(new(kind, "not-run", token.IsCancellationRequested ? "cancelled" : "prior-stage-failed", 0, 0, 0, 0));
                continue;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var model = new ObservedModel();
            var consent = kind == "tool-roundtrip" ? Consent(EgressCategory.UserMessage, EgressCategory.Metadata, EgressCategory.QueryResults)
                : Consent(EgressCategory.UserMessage);
            var session = Session(consent);
            var beforeRequests = transport.Requests;
            var beforeReads = source.Reads;
            var beforeResults = transport.RequestsWithSyntheticQueryResult;
            var beforeRestricted = transport.RequestsWithRestrictedData;
            var marker = "AI_SYNTHETIC_VERIFIED_" + Guid.NewGuid().ToString("N");
            var textSeen = false;
            var lateText = false;
            var revoked = false;
            var finalMarkerSeen = false;
            var finalText = new StringBuilder();
            var message = kind == "tool-roundtrip"
                ? "這是合成驗收。先使用 get_table_details 讀取銷售表，接著使用 execute_dax_query 執行以下固定查詢：\n" +
                  DemoModel.SupportedQuery + "\n收到成功的查詢工具結果後，才回答 " + marker + "。不要臆測數值或改寫查詢。"
                : "這是合成串流驗收。請只回答 " + marker + "。不使用任何工具。";
            var outcome = await RunTurn(provider, model, session, session.Snapshot, message, e =>
            {
                if (e.Kind != EventKind.Text) return Task.CompletedTask;
                if (revoked || cancellation.IsCancellationRequested) lateText = true;
                textSeen |= e.Message.Length > 0;
                if (transport.RequestsWithSyntheticQueryResult > beforeResults)
                {
                    finalText.Append(e.Message);
                    finalMarkerSeen = finalText.ToString().Contains(marker, StringComparison.Ordinal);
                }
                if (e.Message.Length > 0 && kind == "cancel-on-text") cancellation.Cancel();
                if (e.Message.Length > 0 && kind == "revoke-on-text")
                {
                    revoked = true;
                    var old = session.Snapshot;
                    session.Update(old.Target, old.Mode, ImmutableHashSet<Capability>.Empty);
                }
                return Task.CompletedTask;
            }, cancellation.Token);
            var requests = transport.Requests - beforeRequests;
            var restricted = transport.RequestsWithRestrictedData - beforeRestricted;
            var passed = kind switch
            {
                "message-only" => outcome.Code == "ok" && textSeen && model.Reads == 0 && model.Queries == 0 && restricted == 0,
                "tool-roundtrip" => outcome.Code == "ok" && finalMarkerSeen && requests >= 2 &&
                    transport.RequestsWithSyntheticQueryResult > beforeResults && model.Queries > 0 &&
                    outcome.ToolResults.Any(r => r.ToolName == "get_table_details" && r.Success) &&
                    outcome.ToolResults.Any(r => r.Query?.Rows.Length == 3 && r.Success),
                "cancel-on-text" => textSeen && !lateText && outcome.Code == "cancelled" && model.Queries == 0 && restricted == 0,
                _ => textSeen && !lateText && outcome.Code == "stale_permission" && model.Queries == 0 && restricted == 0
            };
            gates.Add(new(kind, passed ? "passed" : textSeen || outcome.Code != "ok" ? "failed" : "not-observed",
                SafeCode(outcome.Code), requests, source.Reads - beforeReads, model.Reads, model.Queries));
            previousStageFailed = !passed;
            // No retries. A failed stage cannot be covered up by later passing requests.
        }
        return Report();

        EgressConsent Consent(params EgressCategory[] categories) => new(profile.Provider,
            categories.ToImmutableHashSet(), DateTimeOffset.UtcNow.AddMinutes(5));
        PermissionSession Session(EgressConsent? consent) => new(DemoModel.Snapshot.Identity, ConversationMode.Analyze,
            ImmutableHashSet.Create(Capability.MetadataRead, Capability.DataQuery), consent);
        AiAcceptanceReport Report() => new(1, DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile.Provider)))).ToLowerInvariant(),
            live, handler is null ? "configured-endpoint" : "injected-test-transport", true,
            gates.Take(4).All(g => g.Status == "passed"), live && handler is null && gates.Count == 8 && gates.All(g => g.Status == "passed"),
            false, gates.ToImmutable());
    }

    private Task<ConversationOutcome> RunTurn(IAiProvider provider, ObservedModel model, PermissionSession session,
        PermissionSnapshot pinned, string message, Func<ConversationEvent, Task> emit, CancellationToken token) =>
        new ConversationCoordinator(model, provider, new(model, model, session), session,
            new(MaxRounds: 3, MaxToolCalls: 6, MaxTextCharacters: 4000, Timeout: TimeSpan.FromSeconds(profile.TimeoutSeconds)))
            .RunAsync(new(Guid.NewGuid(), message, ConversationMode.Analyze, pinned), emit, token);

    private static string SafeCode(string code) => code is "ok" or "cancelled" or "turn_timeout" or "stale_permission" or
        "egress_denied" or "external_provider_disabled" or "provider_error" or "round_limit" or "tool_limit" or "text_limit"
        ? code : "local-rejection";

    private sealed class ObservedCredentials(ICredentialSource inner) : ICredentialSource
    {
        public int Reads { get; private set; }
        public string? ReadSecret(ProviderIdentity identity) { Reads++; return inner.ReadSecret(identity); }
    }
    private sealed class ObservedModel : ISemanticModelReader, IReadOnlyDaxQueryRunner
    {
        private readonly DemoModel model = new();
        public int Reads { get; private set; }
        public int Queries { get; private set; }
        public Task<ModelSnapshot> ReadSnapshotAsync(CancellationToken token) { Reads++; return model.ReadSnapshotAsync(token); }
        public Task<QueryResult> ExecuteAsync(ModelIdentity identity, string dax, QueryLimits limits, CancellationToken token)
        { Queries++; return model.ExecuteAsync(identity, dax, limits, token); }
    }
    private sealed class ObservedHttp(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(inner, disposeHandler: true);
        public int Requests { get; private set; }
        public int RequestsWithRestrictedData { get; private set; }
        public int RequestsWithSyntheticQueryResult { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Requests >= 12) throw new InvalidOperationException();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            if (root.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0 ||
                root.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "system" &&
                    m.GetProperty("content").GetString()!.Contains("demo-sales", StringComparison.Ordinal))) RequestsWithRestrictedData++;
            foreach (var message in root.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("role").GetString() != "tool") continue;
                using var result = JsonDocument.Parse(message.GetProperty("content").GetString()!);
                if (result.RootElement.TryGetProperty("Query", out var query) && query.ValueKind == JsonValueKind.Object &&
                    query.GetProperty("Source").GetProperty("Id").GetString() == "demo-sales" && query.GetProperty("Rows").GetArrayLength() == 3)
                    RequestsWithSyntheticQueryResult++;
            }
            Requests++;
            return await invoker.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) invoker.Dispose(); base.Dispose(disposing); }
    }
}
