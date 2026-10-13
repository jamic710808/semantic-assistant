using System.Collections.Immutable;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Application;

public sealed class ConversationCoordinator(ISemanticModelReader reader, IAiProvider provider,
    ToolDispatcher dispatcher, PermissionSession session, TurnLimits? limits = null,
    ModelContextBuilder? contextBuilder = null, TimeProvider? resultClock = null,
    ModeController? modes = null, ModeSnapshot? modeSnapshot = null)
{
    private readonly TurnLimits turnLimits = limits ?? new();
    private readonly ModelContextBuilder modelContext = contextBuilder ?? new();

    public async Task<ConversationOutcome> RunAsync(ConversationRequest request,
        Func<ConversationEvent, Task> emit, CancellationToken cancellationToken = default)
    {
        turnLimits.Validate();
        var results = ImmutableArray.CreateBuilder<ToolResult>();
        var messages = ImmutableArray.CreateBuilder<ProviderMessage>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        long sequence = 0;
        var textCharacters = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(turnLimits.EffectiveTimeout);
        Task Send(EventKind kind, string text, string? tool = null, string? code = null) =>
            emit(new(1, request.RequestId, ++sequence, kind, text, tool, code));
        async Task<ConversationOutcome> End(string code, EventKind kind, string message)
        {
            await Send(kind, message, code: code);
            return new(request.RequestId, code, results.ToImmutable());
        }

        try
        {
            await Send(EventKind.Started, "開始回合；所有工具由本機檢查權限。");
            if ((modes is null) != (modeSnapshot is null)) throw new ModePolicyException();
            if (modes is not null)
            {
                if (modeSnapshot is null) throw new ModePolicyException();
                modes.EnsureRoute(modeSnapshot, ModeRoute.Conversation);
                if (modeSnapshot.Target != request.Permissions.Target ||
                    ModeController.Get(modeSnapshot.Mode).PermissionMode != request.Mode) throw new ModePolicyException();
            }
            ProviderAuthorization? authorization = null;
            if (provider.UsesExternalNetwork)
            {
                if (provider.Identity is null || request.Permissions.Egress is null)
                    return await End("external_provider_disabled", EventKind.Error, "尚未授權外部 AI 供應商與資料類別。");
                authorization = new(session, request.Permissions);
                try { authorization.EnsureValid(provider.Identity); }
                catch { return await End("egress_denied", EventKind.Error, "外送授權過期、已撤銷或與供應商／端點／模型不符。"); }
            }
            if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4_000)
                return await End("invalid_message", EventKind.Error, "訊息必須為 1–4000 個字元。");
            if (!Enum.IsDefined(request.Mode)) return await End("invalid_mode", EventKind.Error, "未知的對話模式。");
            if (!session.IsCurrent(request.Permissions) || request.Mode != request.Permissions.Mode)
                return await End("stale_permission", EventKind.Error, "回合的目標、模式或授權版本不符。");
            // Metadata is also sensitive. Never read it when capability was not granted.
            string context = "本回合沒有中繼資料讀取授權。";
            if (request.Permissions.Allows(Capability.MetadataRead) &&
                (authorization is null || authorization.Allows(EgressCategory.Metadata)))
            {
                var model = await reader.ReadSnapshotAsync(deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                dispatcher.EnsurePolicyCurrent(request.Permissions);
                if (!session.IsCurrent(request.Permissions))
                    return await End("stale_permission", EventKind.Error, "中繼資料讀取期間授權已變更。");
                if (model.Identity != request.Permissions.Target)
                    return await End("target_changed", EventKind.Error, "目標或版本已改變。");
                dispatcher.EnsurePolicyCurrent(request.Permissions);
                // Formatting strategy runs only after read/egress, session and source guards.
                if (authorization is not null) authorization.EnsureValid(provider.Identity!);
                context = modelContext.Build(model);
            }
            var tools = dispatcher.AllowedTools(request.Permissions);
            if (authorization is not null)
                tools = tools.Where(t => authorization.Allows(ToolPolicyEngine.RequiredEgress(t))).ToImmutableArray();
            for (var round = 0; round < turnLimits.MaxRounds; round++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                dispatcher.EnsurePolicyCurrent(request.Permissions);
                if (!session.IsCurrent(request.Permissions))
                    return await End("stale_permission", EventKind.Error, "本回合授權已撤銷。");
                var called = false;
                foreach (var result in results)
                    ToolPolicyEngine.EnsureResultFresh(result, resultClock);
                if (authorization is not null) authorization.EnsureValid(provider.Identity!);
                var stream = provider.StreamAsync(new(request.Message, request.Mode, context, tools, messages.ToImmutable())
                { Authorization = authorization }, deadline.Token);
                var enumerator = stream.GetAsyncEnumerator(deadline.Token);
                Task<bool>? pendingRead = null;
                try
                {
                    while (await (pendingRead = enumerator.MoveNextAsync().AsTask()).WaitAsync(deadline.Token))
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        dispatcher.EnsurePolicyCurrent(request.Permissions);
                        foreach (var result in results)
                            ToolPolicyEngine.EnsureResultFresh(result, resultClock);
                        if (!session.IsCurrent(request.Permissions))
                            return await End("stale_permission", EventKind.Error, "已丟棄撤銷授權後的 provider 回應。");
                        if (enumerator.Current is ProviderEvent.Text text)
                        {
                            textCharacters += text.Value.Length;
                            if (textCharacters > turnLimits.MaxTextCharacters)
                                return await End("text_limit", EventKind.Error, "文字輸出超過回合上限。");
                            await Send(EventKind.Text, text.Value);
                        }
                        else if (enumerator.Current is ProviderEvent.Call item)
                        {
                            var call = item.Value;
                            if (results.Count >= turnLimits.MaxToolCalls)
                                return await End("tool_limit", EventKind.Error, "工具呼叫已達回合上限。");
                            if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 128 || !callIds.Add(call.Id))
                                return await End("invalid_call_id", EventKind.Error, "工具呼叫識別碼無效或重複。");
                            called = true;
                            messages.Add(new("assistant", "", Call: call) { Round = round });
                            var preview = call.Name == "execute_dax_query" && call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object && call.Arguments.TryGetProperty("dax", out var dax)
                                && dax.ValueKind == System.Text.Json.JsonValueKind.String ? "將執行 DAX：\n" + dax.GetString() : "工具請求已收到。";
                            await Send(EventKind.ToolStarted, preview, call.Name);
                            var result = authorization is not null && !tools.Any(t => t.Name == call.Name)
                                ? new ToolResult(call.Id, call.Name, false, "egress_denied", "工具沒有對應的外送授權。")
                                : await dispatcher.DispatchAsync(call, request.Permissions, deadline.Token);
                            deadline.Token.ThrowIfCancellationRequested();
                            if (!session.IsCurrent(request.Permissions))
                                return await End("stale_permission", EventKind.Error, "已丟棄撤銷授權後的工具結果。");
                            dispatcher.EnsurePolicyCurrent(request.Permissions);
                            if (authorization is not null) { authorization.EnsureValid(provider.Identity!); ToolPolicyEngine.EnsureResultEgress(result, authorization); }
                            results.Add(result);
                            messages.Add(new("tool", result.Summary, Result: result) { Round = round });
                            await Send(EventKind.ToolCompleted, result.Summary, call.Name, result.Code);
                        }
                    }
                }
                finally
                {
                    // Async iterators cannot be disposed while MoveNext is still executing.
                    // Wait briefly for cleanup; delayed provider events are never consumed.
                    var cleanup = DisposeAfterReadAsync(enumerator, pendingRead);
                    try { await cleanup.WaitAsync(TimeSpan.FromMilliseconds(100)); }
                    catch (TimeoutException) { /* Cleanup observes its own errors when the read finishes. */ }
                }
                if (!called)
                {
                    dispatcher.EnsurePolicyCurrent(request.Permissions);
                    foreach (var result in results)
                        ToolPolicyEngine.EnsureResultFresh(result, resultClock);
                    return await End("ok", EventKind.Completed, "回合完成。");
                }
            }
            return await End("round_limit", EventKind.Error, "AI 工具迴圈已達回合上限。");
        }
        catch (ToolPolicyException ex)
        { results.Clear(); messages.Clear(); return await End(ex.Code, EventKind.Error, "工具註冊或政策已失效，停止本回合。"); }
        catch (ModePolicyException)
        {
            results.Clear(); messages.Clear();
            return await End("invalid_mode", EventKind.Error, "操作模式已失效，停止本回合。");
        }
        catch (QueryResultExpiredException)
        {
            results.Clear(); messages.Clear();
            return await End("result_expired", EventKind.Error, "查詢結果已過期，請重新查詢。");
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? await End("cancelled", EventKind.Cancelled, "已取消回合。")
                : await End("turn_timeout", EventKind.Error, "回合已超時。");
        }
        catch (EgressAuthorizationException)
        { return await End(session.IsCurrent(request.Permissions) ? "egress_denied" : "stale_permission", EventKind.Error, "外送授權已失效，停止後續傳送與工具執行。"); }
        catch (Exception)
        { return await End("provider_error", EventKind.Error, "回合執行失敗；沒有顯示內部例外內容。"); }
    }

    private static async Task DisposeAfterReadAsync(IAsyncEnumerator<ProviderEvent> enumerator, Task<bool>? pendingRead)
    {
        try { if (pendingRead is not null) await pendingRead.ConfigureAwait(false); }
        catch (Exception) { /* The read error is handled by the turn, without exposing provider internals. */ }
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Cleanup must not replace the cancellation/timeout outcome. */ }
    }
}
