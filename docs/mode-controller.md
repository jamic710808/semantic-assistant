# 操作模式控制（Mode Controller）

日期：2026-10-13。

## 契約

AssistantMode 定義五種桌面工作流程；ConversationMode 仍只有 Analyze／Report，其他三種是宿主操作，沒有變成可由 AI 直接呼叫的寫入工具。

| 模式 | 路由 | 能力上限 | 使用方法 |
|---|---|---|---|
| 結構與分析 | Conversation | MetadataRead、DataQuery、ExternalTool | 輸入需求；數值查詢與外送另行同意 |
| 報告模式 | Conversation | MetadataRead、DataQuery、ExternalTool | 成功查詢後驗證 ReportSpec；保存 HTML 另需同意 |
| HTML 檔案 | HtmlFile | ArtifactWrite | 使用仍有效且來源相符的查詢結果，勾選匯出同意後保存 |
| HTML 度量值 | HtmlMeasure | MetadataRead、DefinitionWrite | 選取目標表，檢查固定快照 DAX，另行同意寫入 |
| Dashboard | Dashboard | MetadataRead、DefinitionWrite、DataQuery、ArtifactWrite | 開啟計畫；依序預覽、寫入、查詢及匯出，各自同意 |

能力矩陣只是上限；選取模式不產生權限、資料外送 lease 或寫入批准。ModeSnapshot 綁定控制器 ID、版本、模式及模型來源。核心檢查快照仍有效、來源相符、PermissionSession 仍有效及能力交集，未知模式直接拒絕。

## 接入與撤銷

MainWindow 將五種選擇路由到對應流程。AI 回合及 ToolDispatcher 在結構讀取、查詢和串流交付後重新檢查模式；過期結果不交給下一輪 AI 或桌面。HTML exporter 及模型預覽／Dashboard 子視窗也檢查宿主模式。

切換模式撤銷舊 session、查詢／匯出勾選和 AI 提案。尚未過期、來源仍相符的 QueryResult 可以保留供下一個本機流程使用，但匯出與寫入必須重新同意。TTL 不因模式切換延長；來源切換仍清除結果。

匯出與 Dashboard 快捷入口建立明確的本機模式範圍。獨立模型物件編輯器與 MCP 視窗保留原有各自授權；共用ToolRegistry／PolicyEngine現在接入內建三工具及明確選取的MCP，包含手動MCP操作；外部工具仍需獨立回合啟用／外送／逐次操作同意。來源／模式切換會清除匯入並清理程序。[完整契約](tool-registry-policy.md)。

## 驗證與限制

ModeControllerTests 的 21 項案例涵蓋五種路由、未知模式、偽造能力、來源變更、舊快照、讀取／查詢／串流期間切換及過期匯出。模式階段完整腳本為 322 項測試與 9 條 WPF 流程；加入歷史保存後為350項與10條；共用工具階段為380項／11條；模式 smoke 使用自製模型，實際執行 ReportSpec、HTML 保存、獨立度量值預覽／回讀及 Dashboard。封裝後模式流程的證據另記於 verification.md。

未自動操作原生目標表挑選器或人工授權表單，沒有連接真實模型。模式 guard 不是外部 XMLA 交易；已開始的原生 I/O 不保證硬性取消或撤銷副作用。寫入的 revision、所有權、逐次同意、回讀／修復及結果期限契約持續適用。報告使用固定 ReportSpec／HTML＋SVG；沒有以此宣稱完成 WebView2 或 Power BI 原生視覺編輯。
