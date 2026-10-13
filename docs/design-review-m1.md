# 設計審查與 M1 決策

日期：2026-10-12。主代理以 `model=gpt-6.1-sol`、`reasoning_effort=high` 派出唯讀設計審查子代理（Schrodinger）；工具回傳 agent id `01a1306b-34a7-7ac1-a68d-6748b9613335`。這記錄的是派送參數，不是從輸出內容判斷模型身分。

結論：原先的責任切分方向可沿用；原稿不足以直接作為 M1 驗收契約，須先修正下列問題。

| 問題 | 本次處理 | 後續界線 |
|---|---|---|
| UI／工具契約過於抽象 | 自有 C# record、版本 1 事件、request ID、單調 sequence、工具參數 schema、穩定錯誤碼 | M1 沒有外部輸入 bridge；未來新增 bridge 時必須拒絕未知版本及來源 |
| 本機讀取不等於 AI 外送授權 | M1 只有合成模型及本機 deterministic provider；外部 provider 在回合開始即拒絕 | M3 必須另外實作綁定 provider/endpoint/資料類別的外送同意，不能只開 data.query |
| 授權未綁定目標與模式 | PermissionSession 綁定 session ID、版本、模型 ID/revision、mode、capabilities；更新使舊快照失效 | 已開始的同步外部呼叫可能不能硬中止；不得把結果更新成成功，未來真實 adapter 要定義取消能力 |
| ContextBuilder 可能繞過 policy | 沒有 metadata capability 不讀 context；讀前、讀後檢查授權／目標；有摘要長度上限 | 真實 metadata 仍可能敏感；context selection 不等於匿名化 |
| 工具迴圈及取消語意不清 | 順序執行完整 ToolCall；call ID 去重；round、call、text、time、query bytes/rows 上限 | 無自動重試；M3 串流 parser 必須先完整組裝與驗證參數才派發 |
| WebView2 信任邊界缺失 | **M1 改為原生 WPF**，無 JavaScript bridge；模型文字作為 TextBlock 內容 | 未來 WebView2 僅固定本機 origin；驗證來源、封鎖導覽／新視窗、限制遠端資源 |
| 第一版專案數過多 | M1 只有 Contracts、Application、Adapters.Demo、Desktop、Cli，加一個測試專案 | Local／Cloud／MCP／Writer 在具體需求出現時新增，不先堆空殼 |
| Demo／建置條件模糊 | M1 必交無 Power BI、無 AI key 示範；鎖定 .NET 10.0.401；build、test、CLI、WPF smoke script | CI workflow 已撰寫，本機執行不能證明 GitHub Actions 已成功跑過 |
| Clean-room 用詞過度 | 改稱「依觀察行為自行重寫」；沒有搬入廠商程式、模板或素材 | 同一分析者看過反編譯結果，不能宣稱嚴格隔離的 clean-room；公开授權仍待決定 |
| Dashboard 意義容易混淆 | 區分本機分析頁、HTML measure、Power BI 原生報表視覺 | TOM 操作模型不等於操作所有原生報表版面；原生視覺功能須另定 scope |

## ADR-001：M1 使用原生 WPF

理由：先證明可執行的應用核心、權限、工具迴圈與桌面生命週期。原生 WPF 不需要 Node build、WebView2 runtime 或 JS bridge，降低第一階段驗收面。

代價：目前沒有 React 版 chat、Markdown/chart renderer，也未測試 WebView2 bridge。日後加入 WebView2 仍使用相同 Contracts/Application，並補信任來源及跨程序事件驗收；不能宣稱這些在 M1 已成立。

## ADR-002：M1 不註冊真實資料來源

示範銷售模型、固定 DAX 及三列數值都是新專案自製。Demo query runner 只接受固定的示範查詢，不是 DAX interpreter，也不是 Power BI 引擎。桌面啟動時不列舉、不連線、不查詢使用者 Power BI；沒有 writer/MCP/cloud/OAuth/API key。

## ADR-003：取消與撤銷

UI 開始回合後鎖定模式與權限控制；「取消」同時撤銷授權版本並發出 CancellationToken。Core 在每次工具前與非同步讀取後檢查；取消／撤銷後的查詢結果不進入桌面報告狀態。工具 provider／adapter 必須遵守取消契約；不可把 WaitAsync 的等待超時宣稱為已殺死外部原生引擎。

實作測試另抓到 async iterator 的取消競態：MoveNext 尚未完成時 DisposeAsync 可能丟出錯誤，掩蓋 cancelled。已改為觀察待完成讀取後才 dispose，回合只等待最多 100ms 清理，清理不派發晚到事件。自製非合作 provider 測試驗證 timeout 後沒有新 UI event 或工具執行；尚不等於真實原生引擎的取消已驗證。

## ADR-004：先交可追溯表格報告

本輪在 M1 骨架上接續建立本機 HTML 表格匯出。報告從已接受 QueryResult 產生，保留 result ID、模型版本、查詢及合成標記，所有值做 HTML encoding，無模型產生的 script。匯出路徑由 host 決定、由使用者另行勾選，不讓 AI 指定任意路徑。這是 M4 的準備功能，尚未完成 visual spec/chart envelope 的完整 M4。

## 必須保留的驗證界線

M1 通過只代表自行撰寫的骨架與合成流程成立。真實 Power BI discovery、metadata、DAX、RLS、外部 AI 串流／外送政策、雲端身份、MCP、模型写入、rollback 均未驗證。公開授權及第三方依賴盤點完成前不宣稱已公開發佈為開源產品。
