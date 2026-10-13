# 實作分期與驗收

最新接續：[完整定義版本與重啟稽核檢視](source-version-and-recovery-review.md)：來源重綁／角色等變更使舊身分失效；新增唯讀journal CLI標示未結案與損壞紀錄。完整定義不上送，日誌不當作復原授權；真實故障還原仍待驗收。

本輪接續：[雲端查詢期限離線驗收](cloud-query-deadline-acceptance.md)補齊計時器延遲與非合作依賴的完成檢查、來源前後核對及REST取消guard。真實雲端／AI與原生介面重測仍有相應缺口。

最新進展：[執行中取消與逾時引擎驗收](owned-cancellation-acceptance.md)已有9項實際檢查與538項回歸。既有「查詢中取消未驗收」是前輪狀態；伺服器中止原因、實際程序競態、故障修復與外部服務仍保留缺口。

最新接續：[自有Desktop查詢驗收](owned-query-acceptance.md)完成結果列數、完整UTF-8封裝限制、直接adapter期限及提交命令前取消；528項回歸、16個檢查點通過。真正查詢中取消、實際程序競態／故障修復與外部服務仍待完成。

當前接續：原生 Dashboard 主要操作與七項引擎回讀已通過，完整514項回歸、12條WPF流程通過；[證據與未驗收項目](native-dashboard-acceptance.md)。使用者停止了修正後介面重測，待恢復；真實AI依安排稍後設定。以下早期分期、未發佈或整體原生UI待驗收描述為歷史快照，當前覆蓋以本段與scope-audit為準。

最新追加：[合成Desktop保存／重開](desktop-persistence-acceptance.md)已通過20項實際唯讀驗收。首次失敗與修正證據保留；Assistant原生UI、私人模型及外部AI／雲端仍待驗收。

## 最新核對（2026-10-13）

公開來源、GitHub CI與[來源預覽版 r11](https://github.com/jamic710808/semantic-assistant/releases/tag/source-preview-r11)已完成；下文的「尚未發佈／CI未執行／token持久化未完成」保留為分期歷史，不代表目前狀態。r11有457項測試、12條WPF、8條封裝流程及跨本機／CI一致ZIP證據；完整產品仍待真實AI、雲端及部分Desktop/UI驗收。

目前接續項目為[M3供應商驗收工具](ai-provider-acceptance.md)：已加入固定合成任務、明確外送flags、no-consent預檢、實際HTTP／工具結果往返與取消／撤銷檢查。使用者選擇稍後設定供應商，未宣稱真實服務通過。詳細缺口以[目前覆蓋核對](scope-audit.md)為準。

## M0：逆向基線與架構契約（已完成）

- 固定安裝版 build stamp、artifact hash 與 REA Evidence ID。
- 描述本機連線、雲端連線、agent tool loop、權限、Dashboard 與 WebView2 bridge。
- 列出保護造成的未知，沒有使用即時模型內容補猜。
- 本次沒有連接 Power BI、執行 DAX、讀取設定或修改模型。

## M1：可建置的自行重寫骨架（本機驗收已完成）

交付：solution、桌面 shell、contracts、application core、fake model adapter、fake AI provider、CI。

實際採原生 WPF，尚未導入 WebView2/React；設計變更見 [審查與 ADR](design-review-m1.md)。已建置、單元測試、CLI/WPF smoke；CI definition 已建立但尚未在 GitHub 執行。

驗收：

- 在無 Power BI、無 AI key 的環境可啟動 demo。
- 單元測試可驗證工具 schema、權限拒絕、取消、超時與 UI event contract。
- repository 中沒有原廠 binary、反編譯程式碼、token 或私人模型資料。

## M2：本機模型唯讀

狀態：**離線與 Desktop 適配器已實作；自製PBIP的真實Desktop讀取與ADOMD查詢已驗收，使用者互動與其他模型相容性待驗收**。離線檢視使用官方 TOM deserializer；新增 Desktop instance 列舉、使用者選取、TOM 結構讀取與 ADOMD DAX runner。每次操作檢查 port/PID/啟動時間與工作區 port 檔案；變動後要求重新選取，不自動切換模型。WPF 接入 instance/catalog 選取與重新讀取，失敗保留原來源。驗證只使用自行建立的目錄與 fake port inventory，未連線使用者現有模型。

交付：由使用者明確選取本機 Desktop instance；讀取模型中繼資料並呈現表／欄／度量值／關係。

驗收：

- 連線階段只取 metadata，不自動讀事實表列值。
- 斷線、Desktop 關閉、port 改變時可重新列舉並提供清楚錯誤。
- 自動化測試使用 fake adapter；真機驗收時由使用者操作本機測試模型。

## M3：AI provider 與唯讀工具

狀態：**串流、工具組裝、外送授權、OS vault、桌面設定與本機 DAX runner 已實作；外部服務驗收待完成**。OpenAI-compatible SSE 支援 UTF-8／參數碎片與多工具；所有工具待完整 JSON、正常 finish reason 與 `[DONE]` 後才交付。授權綁定 provider、端點、AI 模型、permission session/version、資料來源版本、類別與期限。未授權類別不讀取／不提供對應工具；偽造呼叫在核心拒絕。Windows vault 使用合成 key 驗證；HTTP 用實際 loopback 伺服器與 fake handler 驗證，未使用真實 key／服務。

交付：provider 設定、串流對話、tool call loop、get_table_details、execute_dax_query；新增只驗證計畫的propose_dashboard。

驗收：

- 沒有 model.data.query capability 時，agent 看不到 query tool，手動偽造呼叫也會被拒絕。
- Query 有 timeout、列數與回傳大小上限；UI 顯示將執行的 DAX 與結果摘要。
- API key 只寫入作業系統安全儲存，log 不包含 key、token 或整段敏感結果。

## M4：報告與本機 HTML

狀態：**M4 本機報告範圍已實作**。ReportSpec v1 綁定已授權 QueryResult，接受 Table/Kpi/BarChart/LineChart；JSON 拒絕未知／重複欄位、未知 enum、無效欄位引用與其他結果識別碼。預設模板及桌面選擇使用同一驗證器，數值由結果索引取得，產生 escaped HTML／inline SVG，沒有模型產生的 HTML 或 script。已在瀏覽器確認合成表格、397000 合計與長條圖；獨立 ArtifactWrite 授權保留。圖表目前採固定模板，不接受 AI 自訂任意圖表語法。

交付：report mode、受 schema 限制的 KPI/chart envelope、離線 HTML 輸出。

驗收：

- 前端只繪製通過 schema 驗證的 visual spec。
- 本機 HTML 寫入與 semantic model 修改是兩個獨立 capability。
- 圖表資料必須來自受允許的工具結果或明確標示為示範資料。

## M5：雲端唯讀與 MCP

狀態：**雲端唯讀與 MCP stdio 程式碼及合成驗證已完成；真實服務驗收待完成**。已加入 MSAL 委派登入、租用戶綁定、工作區分頁／資料集選取、XMLA TOM metadata、REST DAX query、大小／timeout 與無明文 token 檔案。MCP 使用明確程序／工作目錄／參數、JSON Schema、catalog 版本與逐次同意；工具沒有自動加入 AI。實際啟動自製子程序驗證，沒有操作原廠或使用者外部工具。詳見 [雲端唯讀](cloud-readonly.md)、[MCP stdio](mcp-stdio.md)。

交付：MSAL 使用者登入、workspace/dataset 選擇、雲端 metadata/query adapter、MCP process adapter。MCP Schema建置／評估已移至CLI worker，加入Windows Job、宿主CPU／記憶體輪詢、牆鐘期限與異常回覆拒絕；[隔離邊界](mcp-schema-isolation.md)另列。

驗收：

- 雲端連線前展示所需登入與租用戶條件；遵守 REST/XMLA 服務端限制。
- 不將 service principal secret、refresh token 或 MCP process env 寫進 log。
- MCP 新工具先列出來源與 capability，不自動授權或升級；若明確匯入AI，仍需當次啟用、獨立外送類別及逐次參數同意。

## M6：可選模型寫入與 Dashboard

狀態：**自有模型物件、Dashboard、固定／動態 HTML 度量值、AI結構化計畫提案與稽核已實作；合成Desktop基本寫入與DAX已驗收；Dashboard／HTML視覺及外部服務待驗收**。獨立 TOM writer 支援 Desktop／XMLA；合成與未連線 TOM 測試覆蓋授權、碰撞、完整版本、operation ID、物件完整性、混合批次回讀／還原、取消及外部相依性。AI只驗證提案，不自動修改或查數值。Dashboard指標寫入回讀後可另行預覽動態DAX；固定快照與本機匯出另需查詢同意。原生報表視覺仍待範圍審查；沒有以demo代替真實服務驗收。詳見 [模型修改與 Dashboard](model-writing-dashboard.md)、[模型物件寫入](model-object-writing.md)、[動態HTML／提案](dynamic-html-dashboard-proposals.md)。

交付：獨立 writer adapter、修改 preview、逐次同意、revision/conflict 檢查、回讀驗證與 rollback。

驗收：

- 全新安裝的 model.definition.write 預設關閉。
- 沒有當次明確同意時，所有寫入工具在本機被拒絕。
- 使用假 adapter 測試成功、碰撞、版本衝突、寫入後驗證失敗與 rollback 失敗。
- 不宣稱外部 XMLA 寫入具有原子交易；partial failure 可見且有修復指引。

## 待決定項目

原設計中仍待補齊的安全token持久化選項與發佈條件，以[覆蓋審查](scope-audit.md)為接續清單；目前主要流程成功不等於全部架構要求完成。ContextBuilder策略已抽離，[預算與授權路徑](context-builder.md)記錄目前實作；[結果TTL](query-result-retention.md)已加入核心、桌面／Dashboard、快照寫入與AI外送邊界；[設定保存](settings-store.md)已保存可選profile與預算而不恢復操作同意；[模式控制](mode-controller.md)提供五工作流程、能力交集與舊回合撤銷；[歷史保存](history-store.md)以明確opt-in、固定摘要／報告索引、期限／容量與清除政策加入桌面；[共用工具政策](tool-registry-policy.md)已接入手動MCP及AI明確匯入。

- 自行重寫來源採 MIT；22 套件依賴清單與來源 ZIP／manifest／SHA256 已加入，第三方二進位分發條件與實際公開 release 尚未完成，見 [來源封裝](release.md)。
- 第一版 AI provider：OpenAI-compatible API、本機模型或兩者並行。
- 第一個 release 明確包含 Windows fixture/demo 模式；Desktop live adapter 尚未驗收。
- 雲端連線是否列入 MVP；目前最穩妥的順序是本機唯讀先完成。

## 真機驗收入口已準備

`New-LiveFixture.ps1`由自行重寫的PowerBIFixture產生全新PBIP與69筆inline資料，附四條DAX預期值及原生writer／Dashboard計畫；[驗收步驟](live-fixture-acceptance.md)及Pending模板保留每個真機gate。11項fixture測試與7個官方JSON Schema已驗證，尚未開啟Desktop或執行真實DAX／SaveChanges。


## 合成Desktop真機進展（2026-10-13）

正式本機adapter與TOM writer已在自製PBIP通過13個檢查點：刷新、metadata、四條ADOMD查詢、無同意拒絕、三種物件SaveChanges／回讀、重複操作收據及新物件DAX。Desktop 2.158.1177.0；[來源核對、失敗原因與驗收界線](live-fixture-acceptance.md)記錄命名衝突、內部列號與inferred metadata雜湊修正。此前段落的未連線描述是前輪歷史證據。

新增11項回歸案例；完整402項單執行緒測試通過。原生Assistant選取操作、Desktop儲存／重啟、查詢限額／取消／競態、真實故障修復、Dashboard／HTML視覺、AI與雲端仍未驗收。`Verify-LiveFixture.ps1`每次建立新模型，結束清理自有程序；不接觸私人模型。


## Dashboard與動態HTML真機驗收（2026-10-13）

最新原始碼在全新自製PBIP通過34個正式檢查點：原13項基本流程、Dashboard預覽／SaveChanges回讀／分別授權查詢與匯出，以及三種動態HTML計畫的12個篩選情境。修正DAX FORMAT產生尾隨小數點、SVG不顯示的問題，增加SVG語法與有限數值檢查；423項回歸測試全部通過。4份實際HTML另經瀏覽器AX與畫面檢視，確認柱形、折線、期間交集與空資料呈現。

權威證據與歷次失敗保留方式見[Dashboard引擎驗收](dashboard-engine-acceptance.md)。TREATAS與本機瀏覽器不能代替Power BI自訂HTML視覺、切片器互動、原生Assistant操作、真實AI或雲端；安全token持久快取、公開remote／CI／release等原始缺口保留。此前的402項與13個檢查點為歷史紀錄。


## Windows雲端登入快取（2026-10-13）

原設計的可選安全token持久化已接入：預設關閉、DPAPI CurrentUser、client／tenant／account pin、30天絕對期限、MSAL通知中序列化及驗證後提交、獨占鎖／加密原子更新、改選帳號與登出／表單清除。一般設定不保存這項勾選、帳號或能力。32項新增測試通過；完整455項及12條WPF流程通過。實際Entra登入／silent refresh／帳號切換、跨Windows使用者及雲端服務仍待驗收。

[登入快取的使用、來源與驗收](cloud-login-cache.md)保留首次NameScope錯誤與修正證據；依賴報告更新為23個套件，分開10個桌面NuGet、1個framework提供與12個開發／測試相依。此前關於token持久化尚缺的描述屬前輪狀態。公開remote、CI／release與第三方二進位分發條件仍未完成。
