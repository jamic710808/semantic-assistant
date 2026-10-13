# Semantic Model Assistant 開源替代架構

狀態：設計提案 v0.2；M1 採分階段實作，修正紀錄見 [設計審查](design-review-m1.md)  
基準：對 ABI Assistant 5.0.2 的靜態逆向；本文件中的新模組與安全控制是重建設計，不宣稱原程式已採用。

## 1. 設計目標

1. 由自己的原始碼建置與發佈，不需 ABI Assistant 廠商帳號、license server、rule pack 或安裝器。
2. AI 只產生結構化工具呼叫；模型連線、查詢與寫入由本機 policy engine 決策。
3. 預設只讀。模型中繼資料、查詢資料、寫本機檔案、修改語意模型是不同權限。
4. Power BI、AI provider、MCP 與 UI 都透過介面隔離，任一服務改版時能替換單一 adapter。
5. 有 fixture、假資料來源與假 AI provider，未連接真實 Power BI 也能開發、測試與展示。

## 2. 建議技術基線

- Windows-first 桌面版：M1 使用原生 WPF；後續需要豐富報告呈現時再引入 WebView2 + React/TypeScript。UI 與業務核心分開；WebView2 bridge 尚未實作。
- Core：.NET 10 LTS。依 Microsoft Learn，.NET 10 LTS 支援至 2028 年 11 月；用於用戶端應用比本機現有 .NET 6 更適合作為新起點。[.NET releases and support](https://learn.microsoft.com/dotnet/core/releases-and-support)
- Power BI adapter：以 Microsoft Analysis Services NuGet client packages 封裝 ADOMD.NET 查詢與 AMO/TOM 模型操作；Microsoft 文件指出新多 runtime 套件支援 .NET Core，並建議使用 NuGet 套件。[Analysis Services client libraries](https://learn.microsoft.com/en-us/sql/analysis-services/instances/data-providers-used-for-analysis-services-connections)
- 雲端登入：使用 MSAL 的 user-delegated flow；token 儲存交由作業系統安全儲存，不放入 repository、prompt、log 或明文設定匯出。
- AI gateway：先定義自己的 provider interface；第一版可支援 OpenAI-compatible endpoint，後續再加 Azure 或本機模型 adapter，不讓 UI 綁定單一模型廠商。
- 版本：鎖定 NuGet/npm dependency，CI 執行 build、unit tests、dependency/license report；由新專案的 release workflow 發佈。

Power BI 雲端有不同的讀取路徑與租用戶條件。Microsoft 的 ExecuteQueries REST API 僅支援 DAX，且有單次一個 query／一個結果表、列數／值數與 15 MB 限制；因此 adapter 應做逐請求上限及清楚錯誤回報。[Execute Queries REST API](https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-queries)

XMLA read/write 是另一條管理路徑；Microsoft 文件指出 read-only 與 read-write 由容量及租用戶設定控制，read access 也需要 semantic model Build 權限。重建版應將讀取連線與模型寫入介面分離，不可因為讀取成功就推定可寫入。[XMLA endpoint connectivity and management](https://learn.microsoft.com/en-us/fabric/enterprise/powerbi/service-premium-connect-tools)

## 3. 系統模組

~~~mermaid
flowchart LR
  U[使用者] --> UI[Desktop Shell<br/>WPF + WebView2]
  UI <--> BR[Typed UI Bridge<br/>JSON events]
  BR <--> APP[Application Core<br/>Conversation / Modes / History]
  APP --> CTX[Context Builder<br/>Metadata budget / selection]
  APP --> AI[AI Provider Gateway]
  AI --> LLM[Cloud or local model]
  APP --> REG[Tool Registry + JSON Schema]
  REG --> POL[Capability Policy + Approval Gate]
  POL --> DISPATCH[Tool Dispatcher + Audit Log]
  DISPATCH --> PM[Power BI Model Port]
  DISPATCH --> FILES[Local Artifact Store]
  PM --> LOCAL[Local Desktop Adapter<br/>ADOMD / TOM]
  PM --> CLOUD[Power BI Service Adapter<br/>MSAL / REST / XMLA]
  APP <--> MCP[MCP Adapter<br/>user-configured process]
  APP --> RENDER[Report / HTML Renderer]
  RENDER --> UI
  APP --> STORE[Settings / Credential Vault / History]
~~~

### 3.1 Desktop Shell 與 UI bridge

Desktop shell 管理視窗、WebView2、檔案選擇器、剪貼簿與系統通知。前端只呈現訊息、工具狀態與已驗證的 report envelope，不直接持有 Power BI connection 或 AI secret。

上述為長期 shell 職責；M1 是原生 WPF，不包含 WebView2、React 或輸入 bridge。未來 bridge 必須只接受固定本機 origin 的訊息，封鎖未授權導覽／新視窗／遠端資源；模型產生的文字不得成為 host command。

Bridge 使用有版本的事件封套，例如 requestId、type、payload、status、error。所有輸入都做 JSON schema 驗證，所有輸出都經 HTML escaping／Markdown sanitizer；不允許模型輸出直接作為任意本機 script 執行。

### 3.2 Application Core

- ConversationCoordinator：管理單一回合、串流取消、tool call/result 往返與最大回合數。
- ModeController：只允許明確模式：Analyze、Report、HTML file、HTML measure、Dashboard。
- ContextBuilder：從已讀取的中繼資料挑選必要表／欄位／度量值，控制 token budget；不預載整張事實表資料。
- ToolRegistry：保存工具名稱、版本、JSON Schema、所需 capability 與結果格式。
- PolicyEngine：在執行每個工具前，按模式、使用者設定、資料來源、工具風險與當次授權重新判斷。
- M1 PermissionSession：綁定 session、授權版本、target ID/revision 與 mode；變更授權使舊回合快照失效。ContextBuilder 也檢查 metadata.read，無授權不讀 context。
- HistoryStore：保存對話摘要與產物索引；secret 與原始資料查詢結果需有獨立保存政策。

### 3.3 Power BI ports 與 adapters

定義最小介面：

- IDesktopModelDiscovery：列舉本機 Power BI Desktop model endpoint。
- ISemanticModelReader：讀模型名稱、表、欄、度量值、關係等中繼資料。
- IReadOnlyDaxQueryRunner：執行受限 DAX query，回傳有列數／位元組／時間上限的結果。
- ISemanticModelWriter：新增或更新模型物件；獨立 package／權限，不注入只讀 agent。
- ICloudWorkspaceCatalog：列 workspace、dataset 與使用者可存取的報表脈絡。

Local adapter 對應目前桌面程式可見的「列舉 Desktop instance → 讀取 metadata → 更新模型樹」流程。Cloud adapter 分成身份驗證、workspace/dataset catalog、metadata load、query runner；使用者 token 只留在本機安全儲存。

### 3.4 AI gateway 與 agent tool loop

一個回合：

1. UI 送入使用者訊息和模式。
2. ContextBuilder 產生中繼資料摘要；只有需要即時數值時才開放 query tool。
3. AI gateway 以串流方式回傳文字或 tool call。
4. ToolRegistry 驗證參數型別；PolicyEngine 檢查 capability、模式、資料來源與授權。
5. Dispatcher 呼叫唯一必要 adapter，記錄工具名稱、耗時、結果摘要與錯誤碼；避免保存敏感列值。
6. 結果作為 tool result 回到 AI，直到完成或達到回合上限。
7. Report envelope 經 schema validation 後由本機 renderer 呈現。

禁止讓模型直接取得連線字串、token、任意 SQL/XMLA 命令、filesystem path 或 model writer 物件。

M1 只用本機 fake provider。預設最多 6 個 provider round、8 次工具呼叫、20,000 字元及 15 秒；查詢最多 100 列、64,000 UTF-8 bytes 及 3 秒。完整參數才派發，順序執行、不自動重試；重複 call ID 拒絕。取消後不再派發、不接納晚到結果。這些限制尚不構成對不合作原生引擎的強制終止保證。

### 3.5 權限模型

| Capability | 預設 | 可執行操作 |
|---|---:|---|
| model.metadata.read | 只對合成示範開啟；真實來源須選取授權 | 讀模型結構 |
| model.data.query | 關閉或逐次同意 | 執行受限 DAX，明確區分讀取資料與讀取結構 |
| artifact.file.write | 關閉或逐次同意 | 寫出 HTML／報告檔 |
| model.definition.write | 關閉 | 建立／更新度量值、欄位、關係等模型物件 |
| mcp.process.start | 關閉 | 啟動使用者設定的 MCP process |

Agent 收到的 tools 只包含已授權 capabilities 的交集；即使模型幻覺工具名稱、參數越權或切換模式，也由本機 policy engine 拒絕。每次模型寫入都要顯示差異、目標模型與回復方法，取得當次明確同意。

本機讀取權限不包含外送 AI 的同意。M3 必須另外綁定 provider、endpoint、資料類別及使用者同意；中繼資料也屬此政策的保護範圍。M1 在建立 context 以前就拒絕外部 provider。未來寫入同意須綁定目標、revision、操作及參數雜湊；不能以全域布林值替代。

### 3.6 報告與 Dashboard

- Report：先輸出受 schema 限制的摘要、KPI、visual spec，再由前端 renderer 建圖。
- HTML file：本機產生獨立檔案；不需模型寫入權限。資料來源仍需由 query capability 明確取得。
- HTML measure：建立 Power BI measure，屬 model.definition.write；和本機 HTML file 使用不同按鈕及授權。
- Dashboard：以 plan JSON 描述指標、維度、週期與視覺；本機驗證欄位、DAX、名稱碰撞及 payload 後，先產生 preview/diff，再由 writer adapter 執行 write → read-back verify → rollback。

部署交易需有 operation id、模型 revision、owned object marker、idempotency key、驗證結果和 rollback log。外部模型不是資料庫交易，因此 rollback 只能盡力還原，UI 必須顯示 partial failure。

此處 Dashboard 是規劃中的分析頁／HTML measure 功能，不能推定能以 TOM 編輯所有 Power BI 原生報表視覺。只有寫入或回讀驗證失敗時才嘗試 rollback；成功時保留產物。

### 3.7 MCP 與本機資料

MCP process 只能由使用者設定和啟動，顯示 command、arguments、工作目錄與 capability；不得把 MCP server 回傳工具自動升權。所有外部工具經相同 ToolRegistry 與 PolicyEngine。

連線時只讀中繼資料。列值只有在使用者要求指標／分析且 query capability 已開啟後才查詢；查詢結果在記憶體中限時使用，預設不寫入 history。

## 4. 建議程式庫結構

下列為長期邏輯切分，不是 M1 要一次建立的專案清單。M1 實際建立 Contracts、Application、Adapters.Demo、Desktop、Cli 及 Application.Tests；詳細路徑見 README。Power BI ports 與 AI interface 暫放 Application，真實 adapter 成形時再拆分。

~~~text
src/
  SemanticAssistant.Desktop/       WPF shell、WebView2、platform services
  SemanticAssistant.Web/           React/TypeScript chat 與視覺呈現
  SemanticAssistant.Contracts/     UI events、tool schema、report envelope
  SemanticAssistant.Application/   conversation、modes、context、policy
  SemanticAssistant.PowerBI/       ports 與共用 DTO
  SemanticAssistant.PowerBI.Local/ local Desktop / ADOMD adapter
  SemanticAssistant.PowerBI.Cloud/ MSAL / REST / XMLA adapter
  SemanticAssistant.AI/            provider interface 與 provider adapters
  SemanticAssistant.Mcp/           process transport 與 tool import
  SemanticAssistant.Reporting/     HTML / chart / file renderer
  SemanticAssistant.Storage/       settings、history、credential vault
tests/
  SemanticAssistant.Application.Tests/
  SemanticAssistant.PowerBI.Tests/
  fixtures/                         假模型 schema 與合成資料
~~~

## 5. 安全與維運取捨

- 安全優先：預設唯讀、逐次寫入批准、工具分權；使用者多一步確認，但降低意外改模。
- 隱私優先：中繼資料先行，列值按需查詢；降低送往 AI provider 的資料量，但需要更好的 context selection。
- 獨立更新：版本與 NuGet/npm 更新由自己的 CI 維護；不需要 ABI Assistant server，但仍需定期更新依賴與安全修補。
- 本機連線：Power BI Desktop adapter 先限定 Windows；Cloud 與 fixture adapter 可被其他平台重用。
- 寫入功能後置：Dashboard 建置比只讀查詢複雜，需 revision、衝突、驗證和 rollback 測試，不納入第一個可用版本。
