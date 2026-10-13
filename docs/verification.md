# 本機驗證紀錄

2026-10-13 最新追加：[自有查詢驗收](owned-query-acceptance.md)修正空結果封裝／最終UTF-8大小與本機adapter自身deadline，完整528項回歸及16個正式引擎／guard檢查點通過。預先取消、連線後但命令提交前取消、注入guard延遲／pin遺失分別記錄；真正in-flight取消與程序競態仍未驗收。

2026-10-13 CI 追加：提交 `8ff43d1` 的 build run `38349739243` 首次失敗於既有 `HungOwnHelperIsKilledAtWallDeadline`：已回傳 `validation_timeout`，但包含程序啟動與清理的總時間 4.12 秒超過測試的 3 秒斷言。測試改用 8 秒整段等待上限，保留 500 毫秒 worker deadline、拒絕錯誤碼及自有 helper 已退出檢查；產品逾時設定未放寬。失敗紀錄保留於 `artifacts/ci-build-r16-failure.log`；後續結果以發佈附加的 verification JSON 為準。本機首次重建亦因仍開啟的驗收視窗鎖住 DLL 而失敗，保留 `artifacts/verification-native-dashboard-ci1.log`；改用全新來源 ZIP 解壓目錄重建，沒有終止或操作使用者停止的視窗。

最新補充：原生 Dashboard 主要流程、獨立同意與動態 HTML 已操作驗收；七項實際引擎回讀通過。編輯／子寫入後舊預覽文字已修正，新增十項定義回歸，完整 **514 passed / 0 failed / 0 skipped**、12 條 WPF 流程通過。修正後原生重測被使用者以 Esc 停止，不宣稱通過。詳細證據與接續界線見[原生 Dashboard 驗收](native-dashboard-acceptance.md)。下方504項及更早狀態為歷史紀錄。

最新補充（2026-10-13）：[原生介面獨立驗收](native-ui-acceptance.md)完成自有 Desktop 選取／取消、結構搜尋／定義、模式撤銷、writer 預覽／未同意拒絕／實際三物件寫入及回讀。一般回歸為 **504 passed / 0 failed / 0 skipped**，12 條 WPF 合成流程全部通過；`artifacts/verification-native-ui.log`。AI 預檢無 HTTP、憑證讀取、模型讀取或查詢。新增的原生宿主使用軟體繪製，未把自動建窗當成 UI 驗收；原生操作與引擎證據分開。下列各輪數字、未連線描述與 CI／發佈缺口為歷史快照，當前公開狀態見 README、release 與保存／重開文件。

日期：2026-10-13；Windows、獨立安裝的 .NET SDK 10.0.401。

## 結果

| 項目 | 結果 | 證據 |
|---|---|---|
| Locked restore | 成功 | `scripts/Verify.ps1`、各專案 packages.lock.json |
| Release solution build | 成功，0 warnings / 0 errors | `artifacts/verification.log` |
| xUnit | **391 passed / 0 failed / 0 skipped** | `artifacts/test-results/m1.trx`；新增11項Desktop fixture與既有30項共用工具政策；既有28項歷史保存與21項模式控制測試；33項設定／預算測試；既有結果期限、隔離Schema與情境策略保留；全部來源為自製fixtures |
| CLI fake loop | 成功；2 次工具；3 列合成數值 | verification.log 的 Outcome=ok；沒有 Power BI 或 AI key |
| WPF 桌面 | 視窗載入；先驗證未授權 query 不執行，再驗證授權 query | `artifacts/wpf-smoke-*.txt` |
| WPF 離線檢視 | 內附 TMDL 載入、名稱搜尋、DAX/關係物件選取、重讀、失敗保留、返回示範與清除舊 query/session | `artifacts/wpf-offline-*.txt`；`artifacts/offline-preview.png` |
| WPF 寫入 | 預覽無修改、未同意拒絕、同意後寫入／回讀與清除當次同意 | `artifacts/wpf-writer-*.txt`；自製記憶體模型 |
| WPF 混合物件 | 度量值／計算欄位／關係批次預覽、逐次同意、寫入／回讀及模型樹更新 | `artifacts/wpf-objects-*.txt`、`artifacts/objects-preview.png`；自製記憶體模型 |
| WPF Dashboard | 計畫預覽、三種授權獨立、修改／回讀、397000合計HTML、動態DAX獨立寫入／回讀、舊子預覽版本拒絕、返回示範清除舊adapter | `artifacts/wpf-dashboard-*.txt`；自製合成模型，不執行動態DAX |
| WPF AI提案 | 關閉數值查詢仍可提案；無自動寫入能力，取消清除提案；來源綁定計畫經獨立預覽／同意後可操作 | `artifacts/wpf-proposal-*.txt`、`artifacts/proposal-flow-preview.png`；本機模擬AI，無外部服務 |
| MCP stdio | 真實啟動／清理自製子程序；initialize、工具 schema、一次性同意、catalog 變動、取消、一般化錯誤 | `McpTests`、`artifacts/wpf-mcp-*.txt`；沒有原廠程序／使用者工具 |
| MCP Schema 隔離 | 真實CLI worker／自製helper；Windows Job、CPU監測／wall timeout、取消／已知PID清理、ref dialect、錯誤／過大回覆；WPF非法參數不能同意 | `IsolatedSchemaValidatorTests`（20項）、MCP WPF流程；不是外部server沙箱，CPU預算有輪詢粒度 |
| 情境策略 | 可替換表名選擇、預算、排序／去重／未知名稱／控制字元；讀取／外送缺少或來源／授權變更時不建立情境 | `ContextBuilderTests`（15項）；無定義或查詢列，桌面／CLI仍用預設策略 |
| 結果期限 | 5分鐘唯讀期限、UTC／monotonic、複製不續期、渲染／匯出／快照／adapter／Dashboard拒絕、回合與HTTP邊界、視窗清除與新查詢 | `QueryResultRetentionTests`（14項）、主視窗／Dashboard WPF smoke；子預覽過期無Attempted修改，不變更OS時鐘或真實模型 |
| 設定保存／預算 | 自有UTF8設定、v1記憶體遷移／原檔保留、future／無效／秘密／授權欄位拒絕、原檔保護、鎖定失敗、預算邊界、Dashboard列數 | `SettingsStoreTests`（33項）；沒有讀寫原廠或使用者私有設定 |
| WPF設定重載 | 自有fixture store、新視窗讀入profile／兩列預算、無session／query／export同意、Demo來源；切回模擬移除保存profile，實際查詢截斷為2列 | `artifacts/wpf-settings-*.txt`；不是完整OS重啟或人工設定表單驗收；所有smoke注入自有store |
| 模式控制 | 五路由／能力交集、未知／舊模式拒絕，讀取／查詢／串流期間變更不交付結果 | ModeControllerTests（21項）；WPF五模式流程使用自製模型 |
| WPF五模式 | 報告規格、HTML保存、模式撤銷、度量值預覽／同意／回讀及Dashboard | artifacts/wpf-modes-*.txt；未操作原生選表對話框，沒有真實模型 |
| 可選歷史 | 類別opt-in、固定摘要／檔名索引、期限／容量、無效檔保留、獨立store重載／mutation lock、清除不刪HTML | HistoryStoreTests（28項）；資料為自製fixture |
| WPF歷史 | 主視窗回合／HTML匯出、政策選項、第二視窗列表／移除／清除、無權限或結果恢復 | artifacts/wpf-history-*.txt；自有store，沒有OS重啟或真實模型 |
| 共用工具政策 | 預設三工具、明確MCP匯入／最多8、風險／結果格式、來源／registry／mode／catalog、逐次同意、晚到取消、結果分類與期限 | ToolRegistryTests（30項）；Source為自製binding及實際MCP子程序 |
| AI＋MCP往返 | 真實loopback TCP/HTTP串流兩輪＋自製MCP子程序；獨立ExternalToolData、未讀模型或query、兩次請求與call計數1 | ActualLoopbackHttpAndMcpChildCompleteTwoRoundTripWithSeparateConsent；credential為空stub，無真實帳號或AI |
| WPF明確匯入 | 選取／移轉client、沒有自動能力、未同意拒絕、逐次批准／結果、回合啟用清除、已知PID handle等待程序退出 | artifacts/wpf-importedtools-*.txt；自有fixture，非人工pickers／同意表單 |
| 模型修改 | 能力及當次同意、完整 revision、所有權／名稱碰撞、idempotency、寫後驗證失敗、取消後 repair、partial failure | `ModelWriteTests`；假 writer，不是外部 XMLA 原子交易證明 |
| 模型物件 | 混合回讀／還原、型別／端點／名稱碰撞、自有型別更新、unsupported type、外部修改後拒絕還原 | `ModelObjectTests`；合成狀態，不驗證資料基數／計算 |
| 實際 TOM stage | 未連線 Database 上建立／還原欄位與關係；完整性涵蓋 annotation／RLS 方向／格式；外部階層相依性拒絕；annotation 重排不誤判 | `TomObjectMutationTests`；沒有 Server.Connect 或 SaveChanges |
| 結構檢視 | 計算欄位 DAX 與實際關係設定可選取檢視；native metadata revision 包含這些欄位 | `ModelObjectTests.ReadOnlyTreeShowsCalculatedExpressionAndActualRelationshipSettings`、reader／projection source |
| 稽核狀態 | intent／verified flush、無 DAX／名稱／資料、intent 失敗禁止修改、結束紀錄失敗保留已驗證成功修改 | `ModelWriteJournalTests`；隨機自有測試目錄 |
| HTML 度量值 | 固定ReportSpec及動態模板、四種視覺／三種聚合、HTML／DAX quoting、8000字預算、來源／碰撞／直接循環拒絕、獨立寫入同意 | `DashboardTests`、`DynamicHtmlMeasureTests`；詞法與模板測試，未驗證真實DAX語意或Power BI HTML顯示 |
| Dashboard提案 | 嚴格JSON、來源與計畫雜湊、無模型修改／數值查詢、無Metadata外送時拒絕偽造提案 | `DashboardProposalTests`、`AiStreamingTests`；DAX語意與物件所有權仍需後續預覽／引擎 |
| TOM 定義投影 | 未連線的自製 Database／Server 物件：annotation 變動保持 readonly revision，但改變完整定義 revision；來源及授權 guard 在讀取／開啟寫入連線前執行 | `TomWriterProjectionTests`；沒有 Server.Connect |
| 雲端 writer scope | 假 token source：preview 只讀，apply 可請求寫入授權，restore 僅 silent；失敗發生在建立 Server 前 | `CloudWriterAuthorizationTests`；沒有實際 MSAL／HTTP／XMLA |
| 桌面視覺 | 已檢視由自身 WPF visual tree 渲染的畫面，內容／控制可見 | `artifacts/desktop-preview.png`；不是 ABI screenshot，也不是整台螢幕截圖 |
| 離線 TMDL | 官方 TOM serialize → reader deserialize；2 tables、1 measure、1 relationship | 單元測試；`artifacts/tmdl-synthetic-snapshot.json` |
| AI HTTP | 真實loopback TCP/HTTP兩輪流程；含分片Dashboard提案JSON完整組裝與本機驗證，沒有數值查詢 | `AiStreamingTests.ActualLoopbackHttpStreamsSyntheticToolRoundTrip`的兩個案例；無真實provider |
| 外送授權 | 訊息限定、不符／過期同意、偽造 query、撤銷、直接無 lease 請求拒絕 | `AiStreamingTests` |
| OS vault | 實際 Windows credential 寫入／讀回／刪除合成 key | WPF smoke；隨機自有 namespace，沒有讀使用者 key |
| Desktop discovery | port fixture、PID／啟動時間改變、非法 port 與 DAX envelope 成功 | `DesktopDiscoveryTests`；沒有列舉使用者現有服務 |
| Cloud | 固定端點、fake tenant/token、secret serialization、query JSON/null/truncation/empty 與 URL escaping 成功 | `CloudAdapterTests`；沒有真實 MSAL 登入或雲端請求 |
| HTML | schema、encoding、CSP、KPI／SVG、來源與寫檔授權成功；瀏覽器顯示合計397000與長條圖 | xUnit；`artifacts/reports/report-20261012-154748-3609ebbdb635412cba0c43b28a811328.html`；瀏覽器 console error/warning 為0 |
| Publish | 本輪重新封裝，包含共用registry／policy、MCP明確匯入、可選歷史、五模式、設定保存、預算、結果期限、Schema helper與情境建構器 | `artifacts/publish.log`、`artifacts/desktop/`；framework-dependent，其他電腦仍需對應 .NET Desktop runtime |
| 封裝後驗證 | 七條設定重載／MCP匯入／歷史／模式／Dashboard／AI提案／MCP流程，從publish DLL與內含CLI helper執行 | `artifacts/package-verification.log`；歷史離線／物件證據另存；無真實DAX引擎 |
| 來源庫產物檢查 | 本輪168個非忽略來源檔案掃描，exe／dll／pbix／pbit／pem／pfx／.env 產物數為0 | 本機git掃描結果見verification.log；不等同完整秘密偵測或授權稽核 |
| Git | 初始化本機 main repository | 尚未 commit/push、沒有 remote |
| GitHub CI | workflow 已建立 | **尚未在 GitHub 執行** |

## 本次確實修正的實作問題

取消與 async iterator disposal 發生競態，原先把 cancelled 錯誤覆蓋成 provider_error。修正後讀取完成才清理；清理等待受限，晚到結果不派發。新增取消後文字、不合作 provider 與授權撤銷回應測試，最後驗證全部通過。

本輪完整測試最初有239項通過，但solution測試的專案評估出現MSBuild「系統資源不足」，整體腳本失敗。已將restore／build限制為單節點、不重用節點，test改為直接選取唯一測試專案；重新跑完整腳本成功。加入情境建構器後再跑254項與全部7條WPF流程，成功結果才作為本表依據。

Windows Job CPU限制原先單獨依賴OS週期檢查，忙迴圈可能延後終止。已增加宿主每50ms輪詢總CPU／private memory與5秒牆鐘期限；資源測試通過，但不宣稱CPU上限有即時精度。

前一輪封裝完成後，新的PowerShell啟動一度因System.Net.Mail／ApplicationInsights程序集載入失敗而終止；未修改系統PowerShell。另指定內建Windows PowerShell執行封裝腳本成功，並以Node讀取實際紀錄確認三條預期結果。本輪加入結果TTL後重新執行完整驗證、publish與三條封裝流程成功；來源掃描為135個非忽略檔案、上述禁止產物0個，這是副檔名與檔名掃描，不是完整秘密或授權稽核。

設定保存階段最終為301項測試、8條WPF流程；來源掃描139個非忽略檔案、禁止產物0個。初次設定測試的文字檢查把合法QueryRows欄位誤當成Rows，已修成完整JSON欄位匹配；最終結果以本輪TRX與完整腳本為準。正常偏好檔與smoke fixture store已分離，所有smoke都注入artifact下的自有目錄。

五模式階段為322項測試／9條WPF流程，封裝五條流程亦通過。歷史保存的初次測試發現serializer寫入數字enum而StrictJson要求命名enum，造成有紀錄的檔案無法重載；已統一讀寫契約，完整驗證為350項／0失敗／0略過及10條WPF流程。初次失敗log保留於artifacts/history-initial-verification.log；最終以verification.log與TRX為準。歷史以固定欄位保存，沒有把AI文字或原始資料加入摘要。重新publish後六條封裝流程全部通過；非忽略來源146檔、禁止產物0個。

共用工具階段新增30項測試，總計380項與11條WPF流程。初次整合把原生偽造呼叫的固定拒絕訊息也禁止回送，造成兩項既有AI測試失敗；已保留原生拒絕契約，MCP參數仍要求獨立外送類別，原生資料不能改標為ExternalToolData。未修改既有兩項測試的驗收條件。初次log在artifacts/registry-initial-verification.log；最終TRX為全部通過。自有WPF visual tree檢視發現權限區擠壓模型樹，已改為保留模型樹與可捲動權限區，匯入smoke加上實際高度檢查；沒有操作ABI或私人模型。最終重新publish後七條封裝流程亦通過；來源掃描151檔、禁止產物0個。

## 套件下載與建置可重現性

M2 套件還原一度停滯。原廠 NuGet flat-container 下載部分檔案很慢；使用 NuGet 官方 CDN 續傳完成 Microsoft.Identity.Client.NativeInterop 0.20.4，並比對 NuGet catalog 的 SHA-512 成功。過程使用暫存 feed；它沒有加入來源庫設定。最後 `Verify.ps1` 已用專案 `NuGet.Config` 的 nuget.org 與 locked restore 成功，沒有停用 NuGet 簽章驗證。

獨立 SDK 與套件快取放在專案外 `D:\KEN\codex\.tools`；正式來源庫鎖定版本，不依賴此機器的暫存 feed。建置產物、快取與測試 log 不提交，`.gitignore` 排除 artifacts/bin/obj。

## 限制與接續

沒有讀取使用者 Power BI 模型、API key、OAuth token 或原廠使用者設定；沒有建立 Power BI connection、執行真實 DAX 或修改模型。所有 TMDL 測試檔均為本專案自行產生，包含測試動態生成與來源庫內附範例。File reader 的 IsSynthetic 固定 false，因為不根據外部檔案自行宣告其為合成；測試來源由本文件、範例 README 與測試產生器證明。

尚未驗收：真實 PBIP/TMDL 版本、原生 Desktop discovery／TOM/ADOMD connection、外部 AI provider／真實 key／模型品質、外送對話框人工操作、cloud、一般外部 MCP 服務、真實 TOM writer/rollback、人工視窗取消／關閉、WebView2、GitHub Actions、公開release及第三方二進位分發條件。SSE／外送 lease／OS vault／ReportSpec／自製 MCP／合成 writer 已驗證，不能用它們代替真實服務驗收。

離線、Desktop、雲端、AI／外送、MCP、模型修改、Dashboard與提案均已接到桌面。本輪11條WPF流程通過。MCP Schema資源隔離、情境建構器、結果TTL、設定保存、五模式、可選歷史與共用工具政策已補入；[完整範圍審查](scope-audit.md)保留安全token持久化選項、真實服務與公開發佈缺口。真實服務驗收獨立進行，不能拿demo成功替代。

本輪沒有自動操作 Windows 原生資料夾挑選器；WPF smoke 使用相同載入函式與內附範例，不能據此宣稱使用者 PBIP/TMDL 或原生挑選器互動已驗收。未接觸使用者 Power BI 或私人模型檔案。

## 原始碼封裝驗證（2026-10-13）

自行重寫來源加入MIT LICENSE／NOTICE；盤點22套件、10個桌面相依與12個開發相依。NuGet cache metadata與lock的contentHash逐一一致，整包SHA512另比對cache checksum；nuspec與內附license／notice位元組另比對實際nupkg。簽章驗證未由此腳本獨立實作，不把原始ZIP hash當成NuGet去簽章contentHash。

新增Verify-Release.ps1：19個路徑允許／拒絕案例、兩份來源ZIP SHA256相同、逐檔manifest全部一致、已有候選拒絕覆蓋。已分別在PowerShell 7與明確啟動的Windows PowerShell 5.1執行；兩版本JSON縮排差異改以同runtime解析後比較，不改動套件審查基線。來源artifact workflow只是定義，尚無GitHub執行證據。來源封裝不含第三方DLL；詳細條款與限制見[來源封裝](release.md)。

## 獨立 Desktop fixture（2026-10-13）

PowerBIFixture產生器、原生writer／Dashboard計畫及真機驗收模板已加入；新增11項fixture測試，本輪完整TRX為391通過、0失敗、0略過，11條WPF流程通過。四表／13欄／5度量值／2關係／69筆inline合成輸入，營收700、成本420、利潤280、數量13；C#獨立計算預期值，不作DAX執行證據。

TMDL經官方TOM序列化／回讀，7個PBIP／PBIR／PBISM JSON以jsonschema 4.23.0驗證各自官方schema，schemas與SHA256在artifacts/live-schema-evidence-current/result.json。獨立驗證工具只裝在被忽略的artifacts目錄，未加入應用runtime或修改系統Python。fixture-manifest逐檔SHA256保留DesktopOpened=false、DaxEngineVerified=false；未自動開啟Desktop、連接使用者模型或執行真實寫入。詳見[測試模型與驗收](live-fixture-acceptance.md)。來源掃描168檔、禁止產物0個；仍不是完整秘密審查。


## 合成Desktop真機進展（2026-10-13）

正式本機adapter與TOM writer已在自製PBIP通過13個檢查點：刷新、metadata、四條ADOMD查詢、無同意拒絕、三種物件SaveChanges／回讀、重複操作收據及新物件DAX。Desktop 2.158.1177.0；[來源核對、失敗原因與驗收界線](live-fixture-acceptance.md)記錄命名衝突、內部列號與inferred metadata雜湊修正。此前段落的未連線描述是前輪歷史證據。

新增11項回歸案例；完整402項單執行緒測試通過。原生Assistant選取操作、Desktop儲存／重啟、查詢限額／取消／競態、真實故障修復、Dashboard／HTML視覺、AI與雲端仍未驗收。`Verify-LiveFixture.ps1`每次建立新模型，結束清理自有程序；不接觸私人模型。

完整一般回歸已通過402項（0失敗／0略過）及11条WPF流程：`artifacts/verification-live-desktop.log`。修正後的新PBIP的7個官方JSON Schema另通過，證據為`artifacts/live-schema-evidence-desktop/result.json`；該純格式工具刻意保留Desktop／DAX驗證false，live成功由獨立紀錄證明。新版來源171檔、禁止產物0個，NuGet仍22套件；雲端唯讀投影同步排除RowNumberColumn，尚無真實雲端連線證據。


## Dashboard與動態HTML真機驗收（2026-10-13）

最新原始碼在全新自製PBIP通過34個正式檢查點：原13項基本流程、Dashboard預覽／SaveChanges回讀／分別授權查詢與匯出，以及三種動態HTML計畫的12個篩選情境。修正DAX FORMAT產生尾隨小數點、SVG不顯示的問題，增加SVG語法與有限數值檢查；423項回歸測試全部通過。4份實際HTML另經瀏覽器AX與畫面檢視，確認柱形、折線、期間交集與空資料呈現。

權威證據與歷次失敗保留方式見[Dashboard引擎驗收](dashboard-engine-acceptance.md)。TREATAS與本機瀏覽器不能代替Power BI自訂HTML視覺、切片器互動、原生Assistant操作、真實AI或雲端；安全token持久快取、公開remote／CI／release等原始缺口保留。此前的402項與13個檢查點為歷史紀錄。


## Windows雲端登入快取（2026-10-13）

原設計的可選安全token持久化已接入：預設關閉、DPAPI CurrentUser、client／tenant／account pin、30天絕對期限、MSAL通知中序列化及驗證後提交、獨占鎖／加密原子更新、改選帳號與登出／表單清除。一般設定不保存這項勾選、帳號或能力。32項新增測試通過；完整455項及12條WPF流程通過。實際Entra登入／silent refresh／帳號切換、跨Windows使用者及雲端服務仍待驗收。

[登入快取的使用、來源與驗收](cloud-login-cache.md)保留首次NameScope錯誤與修正證據；依賴報告更新為23個套件，分開10個桌面NuGet、1個framework提供與12個開發／測試相依。此前關於token持久化尚缺的描述屬前輪狀態。公開remote、CI／release與第三方二進位分發條件仍未完成。
