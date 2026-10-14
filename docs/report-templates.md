# 報告範本與配色：階段 A

日期：2026-10-14。已實作三分頁配置、保存與三種輸出的樣式接線。

## 使用方式

1. 從專案根目錄的 `Start-Semantic-Assistant.cmd` 啟動新版。已開啟的舊視窗需先關閉再啟動。
2. 點「範本」，依序配置「報告度量值」「獨立 HTML」「Dashboard」。
3. 每頁選取版型與配色。三頁互相獨立；切頁保留本次草稿。
4. 按「保存」一次保存三頁設定；取消或關閉會捨棄未保存修改。
5. 後續輸出使用保存的配置。已產生的檔案與模型度量值不會自動重寫。

每種輸出有兩個自有版型，總覽版將 KPI 排在前、圖表採雙欄、表格跨欄；分節版按視覺原順序單欄排列。窄螢幕會回到單欄。

| 輸出 | 版型 | 配色數 |
|---|---|---|
| 報告度量值 | 一頁報告 · 重點總覽 / 章節報告 · 詳細閱讀 | 7 |
| 獨立 HTML | 經營快報 · 總覽 / 分析報告 · 分節 | 7 |
| Dashboard | 極簡商務看板 / 分析明細看板 | 4 |

一般配色：經營藏藍、科技深青、沉穩琥珀、清新翠綠、明快天藍、創意羅蘭、醒目玫紅。
Dashboard：科技深青、石墨、寶藍、勃艮第。名稱與分組參照需求，色碼與版型為本專案自行設計，不宣稱與 ABI 像素一致。

## 預覽

視窗內提供合成布局示意；「開啟完整 HTML 預覽」使用正式的 HtmlReportRenderer，以合成數值產生本機 HTML，交由預設瀏覽器開啟。
每份預覽以獨立名稱保存至 `%LOCALAPPDATA%/SemanticAssistant/template-previews/`，不覆寫既有預覽。
配置與預覽不讀取使用者 Power BI 資料、不向 AI 發送請求。示意圖不是 Power BI HTML 視覺的實際呈現。

## 輸出映射

| 流程 | 取用設定 |
|---|---|
| 主畫面獨立 HTML 匯出 | HtmlFile |
| 主畫面 HTML 度量值產生 | ReportMeasure |
| 主畫面報告規格呈現檢查 | ReportMeasure |
| Dashboard 視窗的檔案匯出、靜態 HTML、動態 HTML | Dashboard |

Dashboard 視窗開啟時固定樣式快照，顯示版型及配色名稱；若要改看板樣式，關閉後修改範本，再重新開啟 Dashboard。
動態 HTML 的 DAX 別名雜湊包含樣式選擇；寫入預覽與計畫雜湊仍包含完整度量值運算式。
結果頁的「報告視覺」控制圖表種類，範本控制排版與配色，兩者均影響後續 HTML 匯出。

## 保存與相容性

`%LOCALAPPDATA%/SemanticAssistant/preferences/settings.json` 增加可選 `Templates` 欄位。
缺少該欄位的既有設定使用預設範本；AI 模型清單、目前模型、預算及憑證識別碼保留。
首次保存至沒有 Templates 的設定檔前，另存 `settings-before-report-templates-<GUID>.json`。
沿用版本 2 的新增欄位方式；較舊執行檔未必能讀取新增欄位，請使用新版啟動入口。

只保存三組版型與配色 ID；未知 ID 或不相容配色會被拒絕。無效、未知版本或無法驗證的原設定檔不覆寫。
先驗證並保存成功，再套用到主視窗；配置不會新增資料查詢、檔案匯出或模型寫入授權。

## 安全與內容限制

- 固定的自有 HTML / CSS / SVG；AI 不能提供任意腳本、CSS 或色碼。
- HTML 使用已驗證 ReportSpec 與有效期內 QueryResult；來源文字與數值保持編碼。
- 配色同時套用文字、背景、卡片、邊框、KPI、圖表標記與軸線。
- 保留本機 HTML 256 KB、靜態 HTML 度量值 8000 字、動態 DAX 8000 字與動態 HTML 64000 字的限制。更複雜樣式會增加大小，超限仍拒絕。
- 靜態 HTML 度量值保存查詢快照，動態 Dashboard 度量值依目前篩選計算；兩者的資料意義保持原有區分。
- Power BI 顯示 HTML 仍需相容視覺；不同視覺對 CSS Grid / SVG 的支援程度須實際確認。
- 文字報告轉換與對話內完整 HTML 預覽屬階段 B，尚未加入；本輪不會把文字中的數字當成已驗證 KPI。

## 本輪交付與驗收界線

- 桌面原始碼編譯完成，0 警告、0 錯誤。
- 產生三個 WPF 分頁的靜態畫面預覽，以及六份正式輸出器產生的合成 HTML、兩份正式編譯器產生的動態 DAX。
- 未新增或執行測試套件；未操作使用者設定或憑證，未連真實 AI 或 Power BI。
- 保存後重啟、取消語意、原生點擊與 Power BI 實際視覺呈現仍待互動驗收，不能由編譯及合成輸出宣稱已通過。

交付目錄：`artifacts/ui-report-templates-20261014/`。
畫面：`templates-measure.png`、`templates-file.png`、`templates-dashboard.png`。
範例：`examples/` 下的 HTML 與 DAX。

可重建合成資料呈現：

```text
SemanticAssistant.Desktop.dll --render-templates-measure <PNG 路徑>
SemanticAssistant.Desktop.dll --render-templates-file <PNG 路徑>
SemanticAssistant.Desktop.dll --render-templates-dashboard <PNG 路徑>
SemanticAssistant.Desktop.dll --render-template-examples <範例目錄>
```

下一步：先完成互動與合成模型驗收，再開始階段 B 的結構化文字報告轉換。
