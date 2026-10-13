# 共用工具註冊與政策（Tool Registry／Policy Engine）

日期：2026-10-13。

## 契約與呼叫鏈

ToolRegistry 以不可變快照記錄工具定義、來源／版本／定義雜湊、能力、風險、結果格式及來源模型。結果格式為TableMetadataV1／QueryResultV1／DashboardPlanV1／McpDataV1，不能由遠端改成模型writer結果。預設只有get_table_details、execute_dax_query、propose_dashboard三個內建工具。ToolPolicyEngine 在廣告工具、派發、預覽／同意之間、完成操作與下一輪AI前重新檢查session、模式、registry版本及程序／catalog。工具描述與readOnlyHint不能替代授權。

內建工具只接受既有三個名稱及其既定能力，不能透過註冊把execute_dax_query標記為MetadataRead。外部工具僅接受McpProcess／UnknownExternalEffects／ExternalTool，最多8個；來源綁定明確的模型ID／revision。註冊只是availability，不授予能力。未知來源、原生寫入工具偽裝、重複alias、過大schema與過期版本拒絕。

AI可見alias為mcp_加上來源／名稱／定義雜湊生成的24個hex字元，符合64字元名稱上限。程序路徑、工作目錄、PID、設定fingerprint及client身份留在本機來源描述，不加入AI工具定義。AI接收經同意的描述與Schema；描述是程序提供的不可信內容。

AI：MainWindow → 明確匯入／當次啟用 → PermissionSession／外送lease → ToolDispatcher → ToolPolicyEngine → McpAiToolBinding → 隔離Schema驗證 → 逐次參數同意 → MCP一次性approval → tools/call → 來源／policy重查 → 結果 → 下一輪HTTP。

手動MCP也在執行時建立選取工具的registry，於approval／call／結果前後套用同一PolicyEngine，保留原有參數preview與一次性approval。低階McpStdioClient仍有自己的程序／catalog／schema／session檢查；它不是公開給AI的任意程序執行工具。模型物件修改的preview／writer契約持續獨立，沒有加入AI工具清單。

## 桌面使用

1. 先選取來源及「結構與分析」或「報告模式」，配置AI端點（配置／來源／模式變更會清除匯入）。
2. 「更多來源與工具」→「MCP外部工具」：自行指定可執行檔、參數JSON與工作目錄，明確啟動／列出。
3. 查看所選工具來源、描述及Schema，按「匯入所選工具供本次AI使用」。確認後client由主視窗接管，設定視窗關閉；沒有呼叫工具。
4. 勾選「允許當次AI提出已匯入工具呼叫」。僅勾選不執行程序工具，完成或取消該回合後清除；可再次勾選啟用下一回合。
5. 真實AI另須在外送表單勾選外部工具資料，才能傳送名稱、描述、Schema、呼叫參數與結果。Metadata／QueryResults同意不包含此類資料。
6. AI提出每個MCP操作時，先驗證Schema，再展示實際目的地、原始名稱、catalog版本、hash及完整參數。勾選當次操作同意後才建立一次性approval與執行。
7. 清除匯入、來源／模式／profile變更或關閉主視窗，撤銷舊registry／能力並關閉自己啟動的程序。catalog通知／重列或程序終止亦使舊binding失效；需清除與重新明確選取，不自動重試或匯入。

匯入及環境設定只在記憶體，不保存或自動恢復。三種本機輸出／修改模式不提供AI外部工具路由。

## 外送、結果與取消

ExternalToolData是獨立外送類別。外部alias的結果禁止QueryResult偽裝，payload必須標記ExternalToolData；原生metadata不能偽裝成ExternalToolData或UserMessage。HTTP適配器本身在credential讀取和請求前也檢查工具／payload及MCP參數；直接提供不符類別的request拒絕。

原生未允許工具的偽造呼叫保留既有契約：不讀模型或查詢，僅向同一provider回傳其自有呼叫參數及固定拒絕訊息。沒有模型payload／rows外送。MCP未授權呼叫不執行，外部資料仍要求獨立同意。

結果接受文字及structuredContent的有界JSON，不跟隨resource link、不執行HTML或把結果指令當宿主指令。副作用可能已發生後才收到失敗／取消／版本通知；無自動重試或交易還原承諾。沒有向AI提供模型writer或程序啟動工具。外部程序仍以目前OS使用者權限執行，不是沙箱。

AI外部工具結果也在記憶體限用5分鐘，UTC／單調時鐘取較嚴限制，複製不續期，序列化與HTTP前重查；回合本身另有更短預算。歷史只記錄固定操作摘要，不保存外部payload。AI文字、先前已送出的內容及手動MCP視窗的文字不是可回收的QueryResult，不宣稱安全抹除。

## 驗證與尚未驗收

ToolRegistryTests共30項，涵蓋明確匯入／無自動能力、8工具上限、偽裝／未知／來源／session／catalog／mode變更、逐次拒絕、晚到同意與取消、結果分類、直接provider的外送檢查及5分鐘期限。真實自製MCP子程序檢查Schema先於同意、拒絕不增加call計數、catalog通知停止後續AI。實際loopback TCP/HTTP加MCP子程序完成兩輪串流往返，未讀模型或執行query；credentialsource是自製空值stub，沒有讀OS或使用者憑證。

WPF匯入流程從來源設定控制到主視窗registry、未勾選拒絕、逐次拒絕、批准後call計數1、回合勾選清除、結果與程序退出；以自己的已知PID handle等待退出。自動流程注入合成同意，不代表人工原生挑選器、import確認窗與逐次同意窗的操作已驗收。完整與publish後結果見verification.md。

一般外部MCP服務與真實AI provider、prompt品質、人工表單及未知副作用仍待驗收；Streamable HTTP transport與MCP profile持久保存沒有實作。schema worker的Job資源限制不涵蓋外部MCP程序。
