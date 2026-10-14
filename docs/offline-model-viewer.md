# 離線 TMDL 桌面檢視

## 本輪範圍

把前階段的檔案 reader 接到 WPF，提供獨立可操作的 metadata 檢視流程。這是 M2 的離線分支，沒有實作即時 Power BI Desktop discovery 或 connection。

## 操作

啟動 `Start-Semantic-Assistant.cmd`（首次先執行 `scripts/Publish-Desktop.ps1`）→「載入內附範例」→ 展開表／欄位／度量值／關係 → 選取 `Total` 查看 `SUM('SyntheticSales'[Amount])` → 搜尋 `Total` → 重新讀取 → 返回示範。

自己的檔案：點選「開啟 TMDL 資料夾」，選 PBIP semantic model 的 `definition` 目錄，讓檔案只在本機解析。檔案挑選器不是 Power BI server 登入介面；選取並不賦予 AI 外送或數值查詢權限。

## 呼叫鏈

`OpenFolderDialog / 內附範例 / 重新讀取`

→ `MainWindow.LoadFolderAsync`（15 秒 deadline、取消按鈕、固定本機 reader）

→ `ModelWorkspace.LoadOfflineAsync`（完成讀取後才 commit；selection generation 排除晚到讀取）

→ `TmdlModelReader`（現有檔案大小、reparse point、讀前後 SHA-256 檢查）

→ `InvalidateConversation`（撤銷舊 session、清除查詢結果與產物授權）

→ `MetadataInspector`（自製文字摘要、物件詳情、名称搜尋）

→ WPF model tree / readonly text box。

沒有任何 AI provider 呼叫；對話、數值查詢與匯出按鈕在離線來源停用。示範回合執行中不能切換来源。重新讀取成功後也會使舊授權失效，不能把不同檔案版本的結果混用。

## 邊界

- 顯示表、欄型別、measure expression 及 single-column relationships；不顯示資料列、partition expression、連線設定、憑證或角色。
- 關係沒有提供基數或 cross-filter direction，因此畫面不推斷這些屬性，也不做模型健康評分。
- DAX 是檔案中的定義，未執行。文字顯示最多 20,000 字元；截斷不修改原檔。
- 搜尋只比對物件名稱，保留命中物件的父節點；不搜尋 DAX 內文。樹的分類數字是原始模型總數，不是搜尋後數量。
- File reader 不自行相信檔案中的「合成」標記；內附範例的來源由其 README 與來源庫證明。
- 失敗／取消保留上一次成功的模型，status 顯示未完成；解析錯誤細節不寫到 UI。
- 不合作的 reader 可能仍在背景完成，但不接納晚到 snapshot；不是硬終止 TOM parser 的保證。
- WPF 自動 smoke 直接呼叫相同載入函式，驗證樹、搜尋、物件選取、重讀、失敗復原及示範切換；**沒有自動操作 Windows 原生資料夾挑選器**，也未驗收使用者實際 PBIP。

## 接續

離線流程可作為後續 provider 外送同意頁的來源；在正式實作外送政策前維持本機檢視。下一個主要工作是建立外部串流解析器的本機 HTTP fixtures、工具參數完整組裝、provider/endpoint/資料類別授權及 OS credential vault。
