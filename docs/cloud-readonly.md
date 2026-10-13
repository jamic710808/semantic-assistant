# M5：Power BI 雲端唯讀

更新：2026-10-13。程式碼已接到 WPF；只完成 fake HTTP／合成 JSON 測試，沒有使用真實 Entra 帳號／Power BI 服務。

## 使用前提

「更多來源與工具」→「登入 Power BI 雲端」。先顯示條件，再填 public-client application ID 與 tenant GUID，由使用者啟動系統瀏覽器登入。需要自有 Entra app registration、http://localhost redirect，以及 Dataset.Read.All／Workspace.Read.All 委派權限。[Microsoft desktop app configuration](https://learn.microsoft.com/en-us/azure/active-directory/develop/scenario-desktop-app-registration)

選取工作區與資料集後，TOM XMLA 讀取表／欄位／度量值與單欄關係。需要適合的容量／PPU、XMLA read 設定與 Build 權限；不因為 REST 登入成功就推定 XMLA 可用。失敗保留先前模型。資料集 GUID 必須與 XMLA catalog ID 相符，沒有以相似名稱猜測對象。[XMLA connectivity](https://learn.microsoft.com/en-us/fabric/enterprise/powerbi/service-premium-connect-tools)

數值查詢使用 ExecuteQueriesInGroup REST，需租用戶啟用 API 並遵守官方單一 query／單一 table 等限制。客戶端再限制 100 列、64 欄、64000 bytes；大於 body 上限的回應直接拒絕，不把不完整 JSON 當成功結果。回傳較多列時只接受最多100列並標記截斷。[Execute Queries In Group](https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-queries-in-group)

## 契約

- MSAL 只用委派使用者流程，沒有 service principal 或 client secret。
- 租用戶 GUID 綁定身份；每次取得 token 後檢查 tenant 與 expiry。
- Token預設只在記憶體。明確勾選記住登入才使用Windows DPAPI、client／tenant／帳號綁定的30天快取；快取不恢復產品操作同意，也不保存到repository、明文設定或log。詳見[登入快取](cloud-login-cache.md)。
- DelegatedToken 的 JSON 忽略 secret，ToString 固定 redacted；REST／XMLA 使用 token header／AccessToken property，不放在連線字串或工具結果。
- REST 目的地固定 api.powerbi.com，GUID 用於路徑，工作區列表每頁200個；下一頁由使用者選取。禁用自動 redirect，遠端錯誤 body 不顯示。
- XMLA endpoint 由已選取的工作區名称做 URL escaping；沒有接收 AI 生成端點。
- 查詢前後比對 metadata revision；此 hash 只涵蓋投影的結構，不代表完整 TOM 定義版本、資料刷新時間或交易快照。
- 空結果保留0列／0欄的真實 envelope，不捏造欄位；本機 HTML 顯示空結果與回傳列數0。
- 讀取雲端模型不自動外送給 AI。AI 分析仍要通過 M3 的逐回合資料類別授權。

## 實際驗證範圍與待辦

已測試固定REST URL、header、tenant／expiry拒絕、token JSON／ToString、不回傳遠端錯誤、null／truncation／多表或錯誤結果拒絕、空結果與XMLA URL escaping。登入快取另完成實際DPAPI、MSAL本機通知與WPF表單驗證；真實MSAL登入、OAuth scope、工作區／資料集API、容量／租用戶限制、XMLA catalog ID與實際語法均待服務驗收。一般外部MCP服務另行驗收，不宣稱M5整體完成。
