# ABI Assistant 靜態逆向分析

日期：2026-10-12  
分析對象：C:\Users\KEN\AppData\Local\Programs\ABI_Assistant  
方式：已安裝檔案的靜態盤點、.NET 組件中繼資料、UI 組件反編譯、WebView2 前端檔案檢視

## 1. 範圍與限制

本次是在分析 ABI Assistant 程式本身，不是分析目前連線之 Power BI 報表或資料模型。過程沒有呼叫 Power BI connector、沒有執行 DAX、沒有讀取資料表列值，也沒有修改 ABI Assistant 的設定或安裝檔。

反編譯輸出只用來理解行為與系統邊界；不會併入新專案。UI 非同步流程經過控制流程混淆，反編譯後可讀但有大量狀態機分支；Core 組件的方法本體多數無法從離線檔案正確解碼。因此以下不是原始碼等價還原。

## 2. 安裝版身分與組成

| 項目 | 靜態觀察 |
|---|---|
| 發行版 | ABI Assistant 5.0.2 |
| Build stamp | 2026-08-27T14:34:46.0940991Z |
| Packaging mode | strict_cloud_rulepack |
| 執行環境 | .NET 6 / Windows x64 |
| 桌面殼層 | WPF |
| 對話／視覺前端 | WebView2，載入本機 WebResources/index.html |
| 前端依賴 | ApexCharts、Marked、Highlight.js 等 |
| Power BI 相關依賴 | Microsoft.AnalysisServices、ADOMD Client、Tabular/TOM 19.84.1 |
| 身分驗證相關依賴 | Microsoft.Identity.Client（MSAL） |

主要組件：

- AIAssistant.UI.dll：桌面 UI 與主要操作流程。
- AIAssistant.Core.dll：Power BI、AI、MCP、設定、報告與 Dashboard 等服務。
- WebResources/index.html：WebView2 對話、Markdown、KPI 與圖表呈現。
- abi_build_stamp.json、abi.integrity.json、abi.integrity.sig：版本與完整性相關檔案。完整性簽章本次未驗證。

REA 保留的目標摘要包含下列 artifact SHA-256，可用來確認之後分析的是同一版檔案：

| Artifact | SHA-256 |
|---|---|
| AIAssistant.Core.dll | 3c1e624b828806b7f75a5f24b29cbdfc6e5a42b135f778c249f57bc4322be6a7 |
| AIAssistant.UI.dll | e56a472a125a2ba7e31f36f8de3c9d5406976d2fb00724cf4456562ac6fc5eb6 |
| WebResources | 1412b2268031497c3dc6333adfb25d22689de33c6b55e46a4e4a0452d0e30ba7 |

## 3. 從 UI 還原的主要操作鏈

### 3.1 本機 Power BI Desktop

靜態 UI 呼叫鏈：

1. UI 呼叫 PowerBIDesktopService.GetActiveInstancesAsync() 列舉本機 Power BI Desktop 執行個體。
2. 使用者選取執行個體後，UI 以其 ConnectionString 和 Name 建立 PowerBIModelService。
3. 呼叫 GetModelMetadataAsync() 取得模型中繼資料。
4. UI 以 PopulateModelTree() 更新左側模型樹；RefreshModel() 也會再次讀取中繼資料並更新樹。

能確認的是「UI 如何呼叫模型服務」；PowerBIModelService 內部如何查詢、如何整理欄位與度量值，因 Core 方法本體不可讀而未能證實。

### 3.2 Power BI Service 雲端語意模型

CloudConnectWindow 的 UI 流程可見：

1. 使用者登入雲端帳戶，列出 workspace，再依 workspace 列出 dataset。
2. 取得所選 dataset 的詳細資料，建立 CloudSemanticModelContext。
3. 探索低成本報表脈絡、載入 schema metadata，並進行連線 smoke test；也會處理關聯報表與 schema 警告。
4. 連線情境交給 MainWindow，後續查詢由雲端服務路徑執行。

以上是靜態程式碼中可見的 UI 流程，不代表本次曾登入雲端或執行這些操作。雲端權限、token 範圍與資料過濾細節仍未知。

### 3.3 AI 對話與工具迴圈

MainWindow 將對話、工具定義、目前模型／報表、AI 設定、API key、endpoint、model name 等參數交給 AppSessionManager.StreamChatWithToolsAsync()。UI 端處理串流訊息、工具呼叫、工具結果，再繼續對話或呈現結果。UI 狀態機中可見 maxTurns 設為 15；精確計數範圍受控制流程混淆影響，尚未完全確認。

這表示產品的主要工作方式不是讓模型直接操作資料來源，而是讓模型提出結構化 tool call，由桌面端程式分派至 Power BI／報告服務。這是由 UI 呼叫鏈支持的架構解讀；Core 的工具解析與模型請求內部實作仍未知。

### 3.4 工具與權限

UI 組件含有以下工具識別字。這是靜態搜尋到的完整候選清單，不代表每個模式都會將所有工具提供給模型。

| 類別 | 可見識別字範例 |
|---|---|
| 模型資訊與查詢 | get_table_details、execute_dax_query、get_dashboard_definition、list_dashboard_measures |
| 模型結構修改 | add_table、add_column、add_measure、add_relationship、add_role、add_calculation_item |
| 模型結構更新／刪除 | update_measure、update_column、rename_object、delete_table、delete_measure、delete_relationship、set_format_string、set_is_hidden |
| 報告與產物 | generate_html_report_file、generate_html_report_measure、generate_dashboard_measures、update_dashboard_measures |

PermissionsWindow 以 AllowModelModification 設定控制模型修改能力；MainWindow 建立修改型工具時也檢查該旗標。雲端路徑另可見 CloudToolPermissionPolicy 條件。這證實程式有修改閘門，但不能據此推論原程式每次修改都會要求逐次人工確認。

execute_dax_query 的工具描述把它標為唯讀查詢，並限制查詢用途；模型物件寫入則以其他工具分開處理。新架構應保留這個能力分界，並加上比單一全域 checkbox 更細的逐次授權。

### 3.5 Dashboard 部署與 HTML 報告

可讀 UI 呼叫鏈顯示 Dashboard 是有狀態的部署工作：

1. 進入 Dashboard 建立或追問更新流程，載入既有定義或執行 preflight。
2. 最後階段要求特定工具呼叫：新建使用 generate_dashboard_measures，追問更新使用 update_dashboard_measures。
3. 模式會篩選可用工具，阻擋混用一般 HTML 報告工具或錯誤的建立／更新動作；連續未完成時停止，不繼續任意執行。
4. UI 呼叫 UpsertDashboardMeasuresAsync()，之後執行 VerifyDashboardDeploymentAsync()；失敗路徑可見 RemoveDashboardMeasuresAsync() 或 RestoreDashboardMeasuresAsync()。
5. 另有 revision、ownership、managed registry、collision、payload validation 等型別名稱。

這支持「編譯計畫 → 寫入 → 回讀驗證 → 失敗清理／還原」的交易式重建方向。DashboardDeploymentTransaction 的 Core 本體不可解碼，所以實際 rollback 原子性與邊界仍不能保證。

HTML 輸出至少有兩種不同結果：產生獨立 HTML 檔案，或建立 Power BI HTML measure。Dashboard 也使用 measure 儲存，因此重建版應清楚區分「寫本機檔案」與「改寫語意模型」兩種權限。

### 3.6 WebView2 前端橋接

本機 HTML 頁面使用 WebView2 message event 接收 addMessage、updateMessage、removeMessage、addSuggestions、clear、setTheme 等事件；前端向 WPF 傳出 webReady、retryClicked、suggestionClicked、convertHtmlReportClicked。視覺呈現包含 Markdown、程式碼高亮、KPI、圖表與主題處理。

### 3.7 MCP、歷史與設定

UI 可見 InitializeMcpAsync() 從 AppConfig 讀取 McpCommand 與 McpArguments，呼叫 McpClientService.StartMcpServer()；啟動失敗會回報 UI。這支持「使用者設定本機 MCP 命令」的描述，但 MCP 傳輸協定、權限與生命週期由於 Core 受保護而未知。

組件還包含 HistoryService、ChatSession、ChatMessage、ConfigService、最多 10 個 AI profile 的常數，以及圖片／Vision 相關服務型別。設定檔位置、secret 加密方式、歷史資料保存政策尚未確認；本次沒有讀取實際使用者設定檔。

## 4. 保護與證據邊界

### 已觀察

- 兩個 .NET 組件中可找到 ConfusedByAttribute 與 Confuser 字串標記。
- 以 ILSpy 9.1.0.7988 靜態讀取 UI MainWindow，可得到方法與呼叫順序，但非同步方法展開成大量混淆狀態機。
- 對 Core 多個方法的反編譯結果出現 Invalid method header、Unknown opcode 等錯誤；不能把輸出的錯誤偽碼當成原始邏輯。
- REA 的 inspect_managed_members 對 UI/Core 均記錄 resource-limit：CLI metadata exceeds 67,108,864 byte representation budget，coverage=unavailable。因此沒有可用的完整方法／call-edge inventory。

### 可合理推論

- 發布版使用某種 .NET 混淆或程式保護處理；Confuser 相關標記與無效 CIL 互相吻合，但本次沒有證明保護器版本或具體轉換方式。
- Core 內部模型存取、AI provider 協議、安全判斷、模板轉換、雲端權限與 license 流程大多不能從目前靜態結果還原。

### 未知／未做

- 沒有執行 ABI Assistant 的逆向 instrumentation、記憶體擷取、網路擷取或改造 DLL。
- 沒有驗證任何 Power BI 資料內容、DAX 結果、模型角色、RLS、報表視覺與真實使用者資料。
- 沒有確認所有模式的工具 allowlist、逐次確認行為、錯誤重試上限及所有 rollback 分支。
- 沒有驗證 abi.integrity.sig 簽章，也沒有讀取 API key、OAuth token 或使用者設定。

## 5. 主要證據索引

| REA Evidence ID | 對象／結果 |
|---|---|
| ev_1ad58a6b0f591263a92f4b40c4731d4e1034f1cafeeb6bb6db87e9c34825863d | Core 組件 PE/.NET 身分與 references |
| ev_898d570c2965bb271d96dd1a02f71e3ce2d4c6dce9162e4d4476346579ee0b25 | UI 組件 PE/.NET 身分與 references |
| ev_3cb9ef49091f29b555101715900fe3bf27e506e0ef9cea311a6c39191aa13daf | WebResources JavaScript/HTML 靜態圖譜 |
| ev_3d5ead18ab63027f5f82f60f0773d329108e39267eed541b70a42bc55027ea93 | Core managed members；因 64 MiB 上限而 unavailable |
| ev_5e88077b85f8ce84893fb6ffa440178547b436fc1c6c88c01f6eede0cdceb61d | UI managed members；因 64 MiB 上限而 unavailable |

本機暫存的反編譯結果位於 C:\Users\KEN\AppData\Local\Temp\abi-assistant-decompiled-ui-main。此資料夾不是新專案來源，也不應被提交至公開 repository。
