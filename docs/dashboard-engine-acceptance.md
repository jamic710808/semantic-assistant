# Dashboard 與動態 HTML 的真機驗收

日期：2026-10-13。本文件描述獨立合成模型的可重複驗收工具與預期值；尚未通過的操作保留 Pending，不以 C# 加總、DAX 字串或 HTML 範例代替引擎執行。

## 執行入口

```powershell
.\scripts\Verify-LiveFixture.ps1
```

每次建立新的 PBIP，啟動及清理自有 Desktop 程序。沿用本機 PID／啟動時間／父程序／工作區／服務埠核對，以及固定 inline M baseline。前輪基本13個檢查點通過證據見 [合成模型驗收](live-fixture-acceptance.md)；此工具接續執行 DashboardDesktopVerification。

生成器另附 `plans/dashboard-default.json`、`dashboard-line.json`、`dashboard-period.json`，及三份 `dynamic-html-*.json` 度量值計畫。`expected-dashboard.json` 保存獨立預期值與 `DaxEngineVerified=false`。先在模型修改視窗新增 Dashboard 的三個指標，再貼入相應 HTML 計畫；每次皆須獨立預覽及同意。生成 DAX 不會執行它。

## 預期數值

| 篩選內容 | 1月：營收／成本／利潤 | 2月：營收／成本／利潤 | 營收／利潤 KPI |
|---|---|---|---|
| All | 330／200／130 | 370／220／150 | 700／280 |
| North | 180／110／70 | 200／120／80 | 380／150 |
| South | 150／90／60 | 170／100／70 | 320／130 |
| Missing | 無資料 | 無資料 | 明確無資料訊息，沒有數值視覺 |

period版本限制 Sales.Date >= 2026-01-01 且 < 2026-02-01，與既有Region篩選交集。KPI因而使用上表1月數值。line版本將原BarChart替換為LineChart；其餘相同。

## 驗收範圍

1. 正式 DashboardCoordinator 預覽未同意時不能修改完整定義。
2. 指標經正式 TOM writer 保存，回讀 owner／operation／完整性。
3. 模型寫入同意不代表數值查詢同意；單獨授權後實際返回兩列月份數值。
4. 數值查詢同意不代表本機檔案匯出同意；另授權後檢查匯出的兩個 KPI 與 CSP。
5. default、line、period三種動態度量值使用正式編譯器／寫入器，逐個執行All／North／South／Missing DAX，最多1列、64KB、30秒。
6. HTML結果按表格欄位與KPI精確比對預期值；檢查SVG圖形數、線段與有限數值座標。解析器禁止DTD、外部實體、script及事件屬性。
7. 每份實際HTML保存到新fixture的 `html-acceptance/`，逐檔保存hash。紀錄中只保留自製資料及已知診斷碼。

引擎TREATAS驗證的是篩選內容（Filter Context）；它不證明Power BI自訂HTML視覺、切片器UI、跨視覺互動或效能已通過。`HtmlVisualVerified=false`及`NativeAssistantUiVerified=false`保留，直到對應UI有直接證據。本機HTML的瀏覽器呈現亦獨立驗收。

## 本輪狀態

- 首次新專案已建立；真機流程在fixture_baseline的TOM連線失敗，尚未進入Dashboard寫入。證據：`artifacts/live-fixtures/SemanticAssistantSynthetic-7395cc40e7c84bc2ba3de7a84463d237/live-verification-a49186a051054c7a9de154cd47ff4ad7.json`。
- 同輪PowerShell／SDK回報Windows資源不足，觀察可用記憶體低於1GB；因果關係尚未證實，沒有將TOM失敗自動判定為DAX或程式錯誤。
- 初版新增7項回歸案例通過：獨立計算、三種計畫DAX長度、HTML數值／結構、空資料，以及外部實體／script拒絕。後續生成計畫案例需以最新建置與TRX另確認。
- 尚待成功連線、完整Dashboard／HTML引擎回合、瀏覽器呈現及Power BI視覺驗收；不變更前輪基本13項成功證據。

## 接續驗收與座標格式修正（2026-10-13）

資源恢復後重新建置，410項回歸測試全部通過。全新專案 `SemanticAssistantSynthetic-16b53510db1f4d4eb324a558799b9b8a` 通過34個真實Desktop檢查點，證據為 `live-verification-de2afcb6d45740a4904041ecc87ada37.json`。包含Dashboard指標寫入／回讀、獨立查詢／匯出同意及default、line、period各四個篩選內容；這次引擎通過不包含瀏覽器繪圖。

後續瀏覽器檢查發現柱形未正常呈現：實際DAX `FORMAT(value, "0.##", "en-US")` 對整數產生 `220.`、`98.` 等座標；C# decimal解析接受尾隨小數點，原驗收器漏掉SVG語法不相容。依 [W3C SVG數值語法](https://www.w3.org/TR/SVG11/types.html#DataTypeNumber)，小數點後需有數字。

修正正式DynamicHtmlMeasureCompiler的座標格式為 `0.00`，保留數值／標籤的原有格式；驗收器先檢查SVG數字語法，再檢查有限decimal範圍。新增13項案例，涵蓋三種計畫的必填小數位、尾點／非數字／千分位／空白／單位／過大指數拒絕，以及整數／小數／負數／指數接受。完整423項測試通過，0失敗／0略過：`artifacts/test-results/dashboard-svg-fixed-full.trx`。

第一次引擎成功與瀏覽器失敗的證據均保留在該fixture，`browser-svg-observation.json` 明確標示繪圖未通過。修正後必須用全新模型重跑，不改寫先前HTML或將其繪圖旗標轉為成功。Power BI自訂視覺與原生Assistant UI仍待獨立驗收。

## 修正後通過的直接證據

- 全新模型：`artifacts/live-fixtures/SemanticAssistantSynthetic-3c1624a6ed3e45a9affd80a0a1f8f736/`。
- 引擎紀錄：`live-verification-acd5936f08cd44f8b6b3146ea004add8.json`，Passed=true，共34個通過檢查點；DashboardLiveEngineVerified及DynamicHtmlFilterContextVerified=true。
- 瀏覽器紀錄：同目錄 `browser-html-observation.json`，4個抽樣產物逐檔SHA256、AX文字與實際viewport檢視：default-All的700／280及兩柱；line-North的380／150及兩點連線；period-South只保留1月150／60及單柱；default-Missing明確無資料且沒有數值視覺。
- 瀏覽器抽樣不是12個組合全數的UI驗收；12個組合全數通過的是DAX結果／HTML結構／數值／SVG語法檢查。截圖由工具當次展示，沒有另存本機影像檔。
- 腳本已結束（exit 0），當次自有Desktop與引擎程序已退出。此前失敗／初版成功紀錄均保留，沒有重送既有操作。

HtmlVisualVerified、NativeAssistantUiVerified、RealAiProviderVerified、CloudVerified仍為false。下一輪優先補入長期架構尚缺的安全token快取選項，再進行對應服務與原生操作驗收；不以本輪34項證據涵蓋全部原始目標。

## 接續方式

資源恢復後，以相同腳本建立全新專案並跑完整回合。只在權威程序狀態為終止時重建；不因觀察超時重啟正在執行的SaveChanges。任何寫入／回讀失敗保留紀錄，核對當次自有物件後再決定修正，不盲目重送。
