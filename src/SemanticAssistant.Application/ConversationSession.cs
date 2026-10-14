using System.Collections.Immutable;
using System.Text.Json;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public static class ConversationContextPolicy
{
    public const int MaximumCharacters = 64_000;
    public const int MaximumEvidence = 128;
    public static void EnsureValid(ImmutableArray<ConversationContextMessage> history,
        ProviderAuthorization? authorization = null, TimeProvider? clock = null)
    {
        if (history.IsDefault || history.Length > 24) throw new InvalidDataException("對話脈絡超過限制。");
        long length = 0;
        var checkedEvidence = new HashSet<ToolResult>();
        foreach (var item in history)
        {
            if (item is null || item.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(item.Content) ||
                item.Categories is null || !item.Categories.Contains(EgressCategory.UserMessage) ||
                item.Categories.Any(c => !Enum.IsDefined(c)) || item.Evidence.IsDefault || item.Evidence.Length > MaximumEvidence)
                throw new InvalidDataException("對話脈絡格式無效。");
            length += item.Content.Length;
            if (length > MaximumCharacters) throw new InvalidDataException("對話脈絡超過限制。");
            if (authorization is not null && item.Categories.Any(c => !authorization.Allows(c))) throw new EgressAuthorizationException();
            foreach (var evidence in item.Evidence)
            {
                if (!checkedEvidence.Add(evidence)) continue;
                if (checkedEvidence.Count > MaximumEvidence) throw new InvalidDataException("對話資料依賴超過限制。");
                ToolPolicyEngine.EnsureResultFresh(evidence, clock);
                if (authorization is not null) ToolPolicyEngine.EnsureResultEgress(evidence, authorization);
            }
        }
    }
}

// Content continuity is independent of PermissionSession. Never persist grants, raw
// tool messages or tool call IDs across turns. Only completed turns enter this session.
public sealed class ConversationSession
{
    private sealed record Turn(ConversationContextMessage User, ConversationContextMessage Assistant);
    private readonly List<Turn> turns = [];
    private ModelIdentity? source;
    private ProviderIdentity? provider;
    public int CompletedTurns => turns.Count;
    public ReportDocument? LatestReport { get; private set; }
    public DashboardProposal? LatestDashboard { get; private set; }

    public void Reset()
    {
        turns.Clear(); source = null; provider = null; LatestReport = null; LatestDashboard = null;
    }
    public void Bind(ModelIdentity target, ProviderIdentity? identity)
    {
        if (source != target || provider != identity) { Reset(); source = target; provider = identity; }
    }
    public void SeedVerifiedChange(ModelIdentity target, ProviderIdentity? identity, string receipt, DashboardProposal? dashboard)
    {
        Reset(); Bind(target, identity);
        var permissions = new PermissionSnapshot(Guid.NewGuid(), 1, target, ConversationMode.Analyze, [Capability.MetadataRead]);
        var request = new ConversationRequest(Guid.NewGuid(), "上一筆由本機回讀驗證的模型修改結果", ConversationMode.Analyze, permissions);
        Complete(request, identity, receipt, [], dashboard, true, false);
    }
    public bool Contains(EgressCategory category) => turns.Any(t => IsFresh(t) && t.Assistant.Categories.Contains(category));

    public ImmutableArray<ConversationContextMessage> Context(ImmutableHashSet<EgressCategory>? allowed, out int omittedTurns)
    {
        var selected = new List<Turn>();
        var dependencies = new HashSet<ToolResult>();
        omittedTurns = 0;
        foreach (var turn in turns.AsEnumerable().Reverse())
        {
            if (!IsFresh(turn) || allowed is not null && turn.Assistant.Categories.Any(c => !allowed.Contains(c)))
            { omittedTurns++; continue; }
            if (dependencies.Concat(turn.Assistant.Evidence).Distinct().Count() > ConversationContextPolicy.MaximumEvidence - 8)
            { omittedTurns++; continue; }
            dependencies.UnionWith(turn.Assistant.Evidence); selected.Add(turn);
        }
        var history = ImmutableArray.CreateBuilder<ConversationContextMessage>();
        foreach (var turn in selected.AsEnumerable().Reverse()) { history.Add(turn.User); history.Add(turn.Assistant); }
        return history.ToImmutable();
    }

    public ReportDocument Complete(ConversationRequest request, ProviderIdentity? identity, string answer,
        ImmutableArray<ToolResult> results, DashboardProposal? dashboard, bool sentMetadata, bool sentExternalTools)
    {
        if (source != request.Permissions.Target || provider != identity || string.IsNullOrWhiteSpace(answer) || answer.Length > 20_000)
            throw new InvalidDataException("回合內容或來源已失效。");
        ConversationContextPolicy.EnsureValid(request.History);
        var evidence = request.History.SelectMany(m => m.Evidence).Concat(results)
            .Where(r => r.Query is not null || r.ToolName.StartsWith("mcp_", StringComparison.Ordinal)).Distinct().ToImmutableArray();
        if (evidence.Length > ConversationContextPolicy.MaximumEvidence) throw new InvalidDataException("回合資料脈絡超過限制。");
        foreach (var result in evidence)
        {
            ToolPolicyEngine.EnsureResultFresh(result);
            if (result.Query is { } query && query.Source != source) throw new DefinitionConflictException();
        }
        if (dashboard is not null && dashboard.Source != source) throw new DefinitionConflictException();
        var categories = ImmutableHashSet.Create(EgressCategory.UserMessage);
        categories = categories.Union(request.History.SelectMany(m => m.Categories));
        if (sentMetadata) categories = categories.Add(EgressCategory.Metadata);
        if (evidence.Any(r => r.Query is not null)) categories = categories.Add(EgressCategory.QueryResults);
        if (sentExternalTools || evidence.Any(r => r.ToolName.StartsWith("mcp_", StringComparison.Ordinal)))
            categories = categories.Add(EgressCategory.ExternalToolData);
        var intent = request.Intent ?? (request.Mode == ConversationMode.Report ? ResponseIntent.Report : ResponseIntent.Analyze);
        var document = new ReportDocument(Guid.NewGuid(), source!, identity, request.RequestId, intent, request.Message, answer,
            results.LastOrDefault(r => r.Success && r.Query is not null)?.Query, evidence);
        var content = $"已完成回合的文件：{document.Id}；回應類型：{intent}。內容不代表操作授權。\n\n" + answer;
        if (dashboard is not null)
            content += "\n\n以下是本機已驗證結構、尚未代表部署成功的 Dashboard 計畫（再次修改仍須重新驗證）：\n" +
                JsonSerializer.Serialize(new { dashboard.Hash, Plan = DashboardProposals.PlanPayload(dashboard.Plan) });
        var turn = new Turn(new("user", request.Message, [EgressCategory.UserMessage]),
            new("assistant", content, categories) { Evidence = evidence });
        if (turn.User.Content.Length + turn.Assistant.Content.Length > ConversationContextPolicy.MaximumCharacters)
            throw new InvalidDataException("回合產物超過對話脈絡上限。");
        turns.Add(turn);
        while (turns.Count > 12 || turns.Sum(t => t.User.Content.Length + t.Assistant.Content.Length) > ConversationContextPolicy.MaximumCharacters)
            turns.RemoveAt(0);
        LatestReport = document;
        if (dashboard is not null) LatestDashboard = dashboard;
        return document;
    }
    private static bool IsFresh(Turn turn)
    {
        try { foreach (var evidence in turn.Assistant.Evidence) ToolPolicyEngine.EnsureResultFresh(evidence); return true; }
        catch (QueryResultExpiredException) { return false; }
    }
}
