# M3：AI 串流與資料外送授權

日期：2026-10-13。實作與驗證範圍是 Chat Completions 相容協定；沒有宣稱所有模型／供應商皆支援。

## 呼叫鏈

WPF 供應商設定 → Windows 認證管理員 → 逐回合外送同意 → PermissionSession → ConversationCoordinator → ProviderAuthorization → HTTP POST → ChatStreamParser → ToolDispatcher／ToolRegistry／ToolPolicyEngine → 工具結果 → 下一轮 HTTP。

串流的 `delta.tool_calls` 可能含不完整參數，官方文件也區分工具 index 與增量片段。本實作累積完整參數、確認 finish reason／`[DONE]`、驗證整批 JSON 後才交付 Call，並在下一輪組成一則 assistant 多工具訊息與各自對應的 tool 結果。[OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling)、[streaming event reference](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events)

## 授權

| 事項 | 檢查 |
|---|---|
| 使用者訊息 | 必須同意；訊息本身可能包含私有資料 |
| 模型結構與 DAX 定義 | MetadataRead 加上 Metadata 外送類別 |
| DAX 查詢與結果列 | DataQuery 加上 QueryResults 外送類別；此類別包含欄位名稱、來源識別及查詢文字 |
| 明確匯入的MCP工具 | ExternalTool＋ExternalToolData；Schema／描述／參數／結果獨立同意，執行前還須逐次操作確認 |
| 外送目的地 | ProviderIdentity 的 ID、完整端點與 AI 模型皆需符合 |
| 模型與回合 | 不可變 PermissionSnapshot 的 session、version、target/revision、mode 與權限；變更即失效 |
| 有效期限 | 最多 10 分鐘；每次請求和串流事件重新檢查 |

缺少同意時，核心在讀 metadata／建立 HTTP 請求前拒絕；僅授權訊息時不讀 metadata、不提供資料工具。適配器要求核心簽發的 lease，直接呼叫沒有 lease 會拒絕。設定供應商只建立本機設定，不連線。

## 上限與行為

- SSE 每行／frame 64000 字元，總串流 2000000 字元，工具最多 8 個、參數 32000 字元、JSON depth 32；dispatcher 再執行個別 schema 的更小限制。
- 回合另限制輪次、總工具数與文字量；桌面外部 AI 回合最多 2 分鐘。
- EOF、缺 `[DONE]`、length/content_filter finish reason、重複 ID、缺 name/type、非物件參數或整批任一非法參數，都不交付工具。
- HTTP 只接受 HTTPS；開發／本機模型允許 loopback HTTP。拒絕 URL userinfo/query/fragment；預設 HTTP handler 不跟隨 redirect。完整端點必須以 `/chat/completions` 結尾。
- key 僅放在 Authorization header，沒有放在 body、prompt、設定檔或診斷事件。遠端錯誤 body 與內部例外不顯示。
- Windows generic credential 使用本程式自己的雜湊命名 target。安全儲存保護靜態憑證；HTTP header 與 managed string 在使用時仍會短暫存在記憶體，不宣稱記憶體中永遠沒有明文。
- 撤銷會拒絕後續請求、工具及晚到結果；取消使用 CancellationToken。已傳送內容無法收回，原生驅動的遠端取消屬 best effort。

[共用工具政策](tool-registry-policy.md)另記錄明確匯入、原生拒絕訊息、MCP獨立類別、來源版本與晚到同意拒絕；MCP結果5分鐘期限也在HTTP前重新檢查。

## 已驗證與缺口

自動驗證涵蓋 2-byte UTF-8 split、參數 split、多工具、整批失敗、錯誤 finish、缺 EOF 終止、取消、oversize、供應商／端點／模型／期限不符、訊息限定、偽造 query、撤銷與 lease 缺失；另以實際 TCP loopback HTTP 伺服器驗證兩輪合成工具流程。

WPF smoke 實際寫入／讀回／刪除隨機命名的合成 credential；沒有讀取既有使用者憑證。外部 provider 支援範圍、真實 key、模型品質及互動授權對話框的人工操作仍待使用者驗收。profile現在可透過「記住這個端點與模型」明確保存；[設定契約](settings-store.md)不保存key、source或操作／外送同意。重載profile不自動連線。可從 Windows 認證管理員刪除 `SemanticAssistant/AI/` 的指定 credential。
