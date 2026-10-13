# 獨立合成 Power BI 測試專案與真機驗收

最新追加：[保存／關閉／重開驗收](desktop-persistence-acceptance.md)已在獨立合成專案通過20項實際唯讀檢查，並修正Lineage Tag保存衝突。下列分期與早期驗收結果保留歷史證據。

日期：2026-10-13。這是自行重寫程式的測試入口。檔案產生、官方 TOM round-trip 與 JSON Schema 檢查已實作；Power BI Desktop 開啟、刷新、DAX 引擎與真實 SaveChanges 不從這些檢查推定完成。

## 產生全新專案

```powershell
.\scripts\New-LiveFixture.ps1
```

腳本建置 `tests/SemanticAssistant.PowerBIFixture`，在 `artifacts/live-fixtures/SemanticAssistantSynthetic-<隨機識別碼>/` 產生全新 PBIP。每次使用新目錄，保留前一份測試專案。資料直接寫在 Power Query（M）`#table` 分割區（Partition），不讀外部檔案、資料庫、URL 或帳號設定。產生器本身沒有 Desktop／雲端連線操作。

此項工作是原始碼中的合成測試資料產生器，採官方 TOM 建構及序列化。模型名稱為 `Semantic Assistant Synthetic Desktop`，模型 annotation 為 `SemanticAssistant.Fixture=desktop-v1`。檔案 marker 是追蹤來源的識別資訊，不是授權、簽章或避免使用者選錯 live instance 的安全沙箱。

產出包括：

- `SemanticAssistantSynthetic.pbip`：入口。
- `SemanticAssistantSynthetic.SemanticModel/definition/`：TOM 序列化的 TMDL。
- `SemanticAssistantSynthetic.Report/definition/`：一張空白驗收頁的 PBIR，無自訂視覺或外部資源。
- `queries/` 與 `expected-results.json`：四條唯讀 DAX 與獨立計算的預期結果。
- `plans/dashboard-plan.json`：可貼入原生 Dashboard 視窗的三指標計畫。
- `plans/measure-changes.json`／`model-changes.json`：可貼入度量值／物件修改視窗的明確計畫。
- `fixture-manifest.json`：逐檔 SHA256；生成時 `DesktopOpened=false`、`DaxEngineVerified=false`。

PBIP 的報表指向專案內自己的 `.SemanticModel` 相對路徑。PBIR 採 `definition.pbir` 4.0、report definition 2.0.0；模型採 PowerBI compatibility mode／1702。格式參照 Microsoft 的 [PBIP 文件](https://learn.microsoft.com/en-us/power-bi/developer/projects/projects-overview)、[報表文件](https://learn.microsoft.com/en-us/power-bi/developer/projects/projects-report)，各 JSON 檔內列確切官方 `$schema` URL。沒有以舊 report.json 的未公開結構生成報表。

## 資料及預期值

| 表 | 列數 | 用途 |
|---|---:|---|
| Sales | 6 | 合成營收、成本與數量 |
| Dates | 59 | 2026-01-01 至 2026-02-28 的連續日期 |
| Regions | 2 | North／South；透過單向多對一關係篩選 Sales |
| Review regions | 2 | 刻意獨立的寫入驗收 utility；測試新增關係前不參與篩選 |

五個基礎度量值：Revenue、Cost、Profit、Margin、Units。數值來源欄位命名為 Revenue amount／Cost amount，避免同表資料欄與度量值同名；來源欄位及關係鍵隱藏。Month 依 Month number 排序；沒有自動授權或自動新增 writer 物件。

| 查詢 | 預期結果 |
|---|---|
| totals.dax | Revenue 700；Cost 420；Profit 280；Margin 0.4；Units 13 |
| regions.dax | North：380／230／150／7；South：320／190／130／6（營收／成本／利潤／數量） |
| months.dax | 2026-01：營收330、利潤130；2026-02：營收370、利潤150 |
| counts.dax | Sales 6；Dates 59；Regions 2；Review regions 2 |

預期 JSON 使用易讀欄名；ADOMD 或 DAX Query View 可以帶有表名／中括號。months 的 MonthNumber 對應 `Dates[Month number]`，比較欄位意義及數值。單元測試由原始六筆輸入獨立加總，不能將 C# 的加總當成已執行 DAX 的證據。

## 真機驗收順序

### 1. 開啟與刷新（M2 準備）

1. 開啟剛產生的 `.pbip`，以此專案進行驗收；記錄 Desktop 版本、專案目錄及開啟的程序／視窗。
2. 按刷新，確認四個 inline M 分割區成功。若 Desktop 報錯，記錄錯誤與確切版本，先修 fixture 或相容性，不能改填 Pass。
3. 在 DAX Query View 逐一執行 `queries/*.dax`，比對表中預期值。這驗證 fixture 的 DAX 引擎語意，仍不等於 Assistant 的 ADOMD adapter 已驗收。

### 2. Assistant 本機唯讀（M2／M3）

1. 啟動自行重寫的 Semantic Assistant，明確選取這個新專案所屬 Desktop instance／catalog。
2. native 模型樹應顯示基礎四表、13欄、5度量值、2關係；實際 Desktop 可能賦予 GUID catalog 名稱，不能只憑檔案名判斷來源。
3. 初始不勾查詢、匯出或寫入；先使用模型樹／詳情驗證 metadata 路徑。
4. 本機 Demo AI provider 固定針對原示範模型；它不適用此英文測試模型。真實 AI 回合需明確設定 provider，逐回合分別同意外送類別。原生 Dashboard／writer 流程可以不用 AI key。
5. ADOMD 查詢需由 Assistant 的授權工具路徑或原生 Dashboard 取得；另記錄列數限制、timeout、取消、來源版本改變時的拒絕結果。沒有實際操作則保留 Pending。

### 3. 模型寫入（M6）

各組測試可重新產生專案，讓 baseline 不受上一組變更影響。計畫使用 `SA_FixtureCheck`／`SA_Fixture` 命名；原有 Revenue 等基礎度量值保持自己的未受管物件身分。

1. 度量值修改視窗貼入 `plans/measure-changes.json`；先預覽、取消，確認沒有新增物件。
2. 再預覽並同意一次，檢查回讀／owner／operation id／hash。新度量值 `[SA_FixtureCheck Revenue]` 應為700。
3. 物件修改視窗貼入 `plans/model-changes.json`；逐次同意後，新增計算欄的合計應為280，新增關係指向 Review regions；用該表 Region 篩選時 Revenue 應分別為380／320。
4. 重新預覽同一計畫檢查可重入／碰撞契約；另以原生 Power BI 修改自有物件後，再嘗試舊預覽，應拒絕版本／完整性衝突。
5. 沒有實際故障注入及回讀證據時，不宣稱 rollback 已在真實服務通過。測試生成器不自動覆寫／刪除使用者物件。

### 4. Dashboard／HTML（M4／M6）

1. 在 Dashboard 視窗貼入 `plans/dashboard-plan.json`，先檢查計畫；只有明確同意後才新增三個自有指標。
2. 寫入成功後另同意查詢，按月份應回傳兩列；Revenue KPI為700、Profit KPI為280。
3. 再另同意本機HTML匯出，檢查 Table／KPI／Bar 視覺；匯出完成不代表 HTML measure 在 Power BI 視覺已驗收。
4. 動態HTML度量值再經獨立預覽／同意。使用相容HTML視覺並加入Regions.Region切片器，North時應營收380／利潤150，South時應營收320／利潤130。
5. 文字跳脫、結果TTL及自有物件回讀在核心已有合成測試；真實HTML視覺／切片器／引擎效能另記錄。

## 紀錄方式與本輪證據

以 `examples/synthetic-desktop/acceptance-template.json` 作紀錄底稿。只填實際已執行的 gate；保留 Pending、Fail、版本、操作及預期／觀察值。不要把尚未執行的Desktop、雲端、AI、HTML視覺或公開CI填為通過。

前輪11項 PowerBIFixtureTests及7個官方JSON Schema檢查為靜態證據；後續真機證據與修正見下節。生成時的manifest保留DesktopOpened=false、DaxEngineVerified=false，以獨立執行紀錄保存後續事實。

## 已執行的 Desktop 驗收（2026-10-13）

```powershell
.\scripts\Verify-LiveFixture.ps1
```

此腳本每次產生新PBIP，只啟動該檔案的新Desktop程序。固定Program Files安裝路徑、PID、啟動時間、引擎父程序及工作區／服務埠；沒有改用已存在的Desktop。載入完成後，先核對marker、四張表所有固定inline M、欄位／量值與關係，再刷新。任一來源或結構不符即拒絕查詢與寫入。這是自有fixture驗收工具，紀錄檔不是任意模型的安全授權證明。

實際Desktop版本：**2.158.1177.0**。通過證據：`artifacts/live-fixtures/SemanticAssistantSynthetic-64b33b32eefa4964a397c5bd3c1985ed/live-verification-e14a8dd9333141abb439969e9aac78d1.json`。13個檢查點全部Pass：

- 新程序／子引擎來源核對、固定baseline、實際inline刷新。
- 正式DesktopModelAdapter模型投影與ADOMD四條預期值查詢。
- 無approval的預覽拒絕，完整定義未變。
- 正式ModelWriteCoordinator＋TomDefinitionWriter混合SaveChanges、owner／operation／integrity回讀，以及相同操作的already_applied收據。
- 新度量值700、計算欄合計280、新關係North380／South320。

腳本結束只清理自己的新程序；尚未驗證Desktop儲存模型變更到磁碟或重啟後恢復。NativeAssistantUiVerified=false；真實AI、雲端、Dashboard／HTML視覺、故障修復、限額／取消及外部修改衝突仍Pending。上述13項不是驗收模板12個大項全部完成。

### 真機發現與修正

1. 初版fixture的Revenue／Cost欄與同名量值被Desktop拒絕；修改來源欄名為Revenue amount／Cost amount並同步固定DAX，保留失敗候選與紀錄。
2. 引擎會自動增加RowNumberColumn；模型樹投影排除此型別，驗收只允許每表最多一個，仍精確檢查所有來源欄位。[Microsoft型別文件](https://learn.microsoft.com/zh-tw/dotnet/api/microsoft.analysisservices.tabular.rownumbercolumn?view=analysisservices-dotnet)。
3. 初次實際SaveChanges成功後，被包含推導型別／處理狀態／時間戳的雜湊誤判為partial_failure。以唯讀工具核對三物件，再以全新模型驗證修正；沒有重送失敗操作或改寫舊物件的stamp。
4. 物件完整性及完整定義版本改用官方SerializeOptions排除IgnoreTimestamps／IgnoreInferredObjects／IgnoreInferredProperties，保留使用者定義、格式、註解、安全篩選與欄型別。[Microsoft序列化選項](https://learn.microsoft.com/en-us/dotnet/api/microsoft.analysisservices.tabular.serializeoptions?view=analysisservices-dotnet)。物件stamp加入definition-v2前綴；舊stamp不自動信任或改寫，仍需人工核對。
5. Desktop服務埠出現早於專案載入完成；腳本先等待自有專案視窗標題，再核對模型baseline。載入失敗不改用其他catalog。

本輪新增11項回歸案例，完整402項測試在單執行緒回合0失敗、0略過。並行真機載入與測試曾造成兩個既有5／8秒預算案例超時，保留failed TRX；隔開真機負載後重跑，不調大產品預算。`--inspect-owned`只在固定baseline與已知三寫入物件範圍內保存唯讀診斷，沒有通用連線命令。
