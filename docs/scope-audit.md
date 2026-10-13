# 原始架構覆蓋與完成條件

日期：2026-10-13。依據 `open-architecture.md`、`roadmap.md`、`design-review-m1.md` 及目前原始碼重新核對。**本文件不是把目前已完成部分重新定義為全部目標。**

## 判定方式

- 程式碼＋合成驗證：所列功能有可追蹤實作與相應測試，不代表外部服務已驗收。
- 部分：有相關流程，但原設計命名、保存政策或共同抽象仍缺少。
- 待實作：沒有足以支援原始要求的程式碼。
- 待真機驗收：目前證據不足以證明 Desktop／AI／雲端／第三方服務的行為。
- 長期呈現選項：原文件或ADR明確分期的項目；保留其邊界，不宣稱已實作。

## M0–M6 對照

| 要求 | 實作／權威證據 | 判定與缺口 |
|---|---|---|
| M0 安裝版本、artifact hash、REA evidence與可觀察呼叫鏈 | `reverse-analysis.md`、靜態證據索引 | 文件完成；受保護Core方法的演算法與prompt未知，沒有以猜測補齊 |
| M1 自己可建置的來源、桌面、合成模型／AI、CI | solution、`Verify.ps1`、CLI／WPF、workflow | 本機可驗證；GitHub CI未執行，沒有公開release |
| M2 使用者明確選取Desktop instance及metadata | `LocalDesktopDiscovery`、`DesktopModelAdapter`、WPF來源選取 | 自建PBIP的實際Desktop連線／metadata已驗收；原生來源選取UI、PID競態與使用者PBIP相容性待驗收 |
| M3 串流、UTF8／碎片／多工具組裝、取消／回合限制 | `ChatStreamParser`、`ConversationCoordinator`、`AiStreamingTests` | 程式碼＋真實loopback HTTP；真實AI供應商／品質未驗收 |
| M3 外送類別、provider／endpoint／model／source版本與期限 | `ProviderAuthorization`、`PermissionSession`、`AiDialogs` | 本機／合成拒絕與撤銷測試；人工對話框操作未驗收 |
| M3 API key OS安全儲存 | `WindowsCredentialVault`、WPF合成憑證往返 | 合成namespace實際驗證；沒有讀取使用者key |
| M4 report envelope／HTML file與獨立授權 | `ReportSpec`、renderer／exporter、`ReportSpecTests` | 四種視覺、來源索引、CSP／跳脫、檔案授權＋合成瀏覽器驗證 |
| M5 delegated cloud登入、catalog、XMLA metadata／REST query | Cloud adapter、MSAL、`CloudAdapterTests` | 程式碼＋假HTTP／token；真實tenant、容量、Build、REST/XMLA與帳號切換未驗收 |
| M5 MCP process、來源、schema、逐次同意、取消 | `McpStdioClient`、`McpToolsWindow`、自製FixtureHost | 真實自製子程序＋WPF；一般外部server與其他transport未驗收 |
| M5 外部schema驗證資源隔離 | `IsolatedSchemaValidator`、CLI worker、Windows Job、獨立測試 | 實際子程序、CPU／wall budget、終止與回覆拒絕；不是外部MCP程序的沙箱 |
| M6 writer與預設關閉能力、預覽、revision／owner／operation ID | `ModelWriteCoordinator`、TOM writer、WPF writer | 自建PBIP已通過三種自有物件及Dashboard指標／HTML的SaveChanges、回讀與實際DAX；雲端及真實故障修復待驗收 |
| M6 idempotency、回讀、修復／partial failure、audit | ModelWrite／Object／Journal tests | 故障／取消／外部修改／相依性測試；沒有持久模型備份或重啟自動還原 |
| M6 Dashboard指標／維度／週期／視覺 | `DashboardPlans`、coordinator、WPF | 自建PBIP的固定計畫已通過正式查詢／匯出與三種動態計畫；任意計畫及原生UI待驗收 |
| M6 HTML measure與HTML file分權 | 固定快照與動態編譯器、獨立子寫入視窗 | 自建PBIP已通過12組真實DAX篩選內容及4份瀏覽器抽樣；Power BI切片器／自訂HTML視覺仍待驗收 |
| AI結構化Dashboard提案 | `propose_dashboard`、`DashboardProposals`、分片loopback與WPF | 只驗證計畫、來源與完整成功回合；無自動寫入或授權升級 |

## 架構層尚未補齊的要求

| 原設計項目 | 現況 | 接續交付與驗收 |
|---|---|---|
| ModeController：Analyze／Report／HTML file／HTML measure／Dashboard | AssistantMode五工作流程、ModeController來源／版本與能力交集、核心及WPF路由／子預覽guard | 已實作＋21項測試與桌面合成驗證；ConversationMode仍為兩種AI對話，後三者不自動查詢／寫入；原生I/O非模式交易 |
| ContextBuilder：選擇與預算 | ModelContextBuilder／IContextTableSelector 已分離；預設依序／偏好表名策略，最多20表／2000字元；受原有讀取與外送閘門保護 | 已實作＋合成驗證；不含定義或數值列，工具按需取得細節；真實模型選擇品質另驗收 |
| SettingsStore | AssistantSettings v2／FileAssistantSettingsStore，AI端點／模型明確選擇保存；摘要／查詢／回合預算；v1記憶體遷移與原檔保留；桌面重載 | 已實作＋合成驗證；key仍在OS vault，source／capability／外送／寫入同意不保存。真實端點／人工設定對話框另驗收 |
| HistoryStore：摘要與產物索引 | HistoryPolicy／FileHistoryStore、桌面列表／政策／移除／清除；回合固定欄位與HTML檔名索引 | 已實作＋28項合成測試與桌面流程；兩類保存預設關閉，1–30天／1–100筆，沒有文字／DAX／列／key／權限；到期讀取過濾、下一次更新整理實體檔，HTML保留 |
| 查詢結果在記憶體限時使用 | QueryResult唯讀期限、UTC／monotonic雙重檢查；桌面與Dashboard定時清除，匯出／固定HTML／AI前重查；快照子預覽綁定期限 | 已實作＋合成驗證；結果5分鐘期限獨立於外送lease，不能續用過期結果；已匯出檔案、先前外送與文字回覆不屬可回收暫存，詳見retention文件 |
| MCP與共同ToolRegistry／PolicyEngine | ToolRegistry／ToolPolicyEngine、內建三工具＋最多8個明確匯入MCP、來源／registry快照、風險、單次同意、ExternalToolData | 已實作＋30項測試、真實自製程序／loopback HTTP與桌面流程；手動MCP同用政策，沒有自動升權／重試；一般MCP服務、真實AI與人工表單另驗收 |
| MSAL token OS持久安全儲存 | 預設記憶體；明確opt-in後DPAPI CurrentUser、client／tenant／account pin、30天期限、獨占鎖／加密原子替換與登出清除 | 已實作＋32項本機測試與WPF表單流程；快取不恢復操作同意。真實Entra／silent refresh／帳號切換及跨使用者環境待服務驗收，見cloud-login-cache.md |
| 自行維護release、dependency／license report | MIT LICENSE／NOTICE、22 套件精確版本與條款／hash、來源 ZIP＋manifest／SHA256、19 路徑驗證與兩份同環境 ZIP 相同、手動來源 artifact workflow | 本機來源封裝已實作；第三方二進位分發條件、跨環境重現性、內容祕密審查、remote／commit／真實 CI／公開 release 尚未完成，見release.md |

上列缺口保留為實作工作，沒有因單元測試通過而移除。部署目標／公開位置與第三方服務驗收仍需使用者提供具體環境。

## 與原始範圍的區別

- WPF替代WebView2／React是既有ADR；後者與bridge信任來源屬原文件明確分期的長期呈現選項，現有WPF不能作其驗收證據。
- 原 `open-architecture.md` §3.6 明確描述Dashboard為分析頁／HTML measure，不能推定可用TOM編輯所有Power BI原生報表視覺。PBIR頁面編輯不是已完成的功能，亦不從未知原廠方法推導必要API。
- 本機模型、雲端、writer與MCP必須各有真實服務驗收證據；假adapter不能證明RLS、基數、效能、XMLA競態或副作用回復。
- 來源庫是依觀察行為自行重寫；不是嚴格clean-room，也沒有證明與ABI Assistant所有功能／prompt／模型品質等價。

## 下一個工作順序

1. MCP隔離與context策略已實作；完整驗證證據見verification，保留CPU預算與OS檢查粒度的限制。
2. 結果TTL與設定保存已補入桌面／核心；五模式控制及可選歷史已接入；保持獨立同意與索引清除邊界。
3. 共用工具契約與MCP明確匯入／逐次同意已接入；保持外送類別與來源／catalog失效邊界。
4. 獨立Desktop fixture產生器／69筆inline資料／預期DAX／native計畫／Pending驗收模板已加入，11項fixture測試與7個官方JSON Schema通過；接續真實Desktop／ADOMD／writer驗收，不連接既有私人報告。
5. 公開授權、第三方條件、release與實際CI／發佈；目的地與服務端條件不足時不宣稱完成。


## 合成Desktop真機進展（2026-10-13）

正式本機adapter與TOM writer已在自製PBIP通過13個檢查點：刷新、metadata、四條ADOMD查詢、無同意拒絕、三種物件SaveChanges／回讀、重複操作收據及新物件DAX。Desktop 2.158.1177.0；[來源核對、失敗原因與驗收界線](live-fixture-acceptance.md)記錄命名衝突、內部列號與inferred metadata雜湊修正。此前段落的未連線描述是前輪歷史證據。

新增11項回歸案例；完整402項單執行緒測試通過。原生Assistant選取操作、Desktop儲存／重啟、查詢限額／取消／競態、真實故障修復、Dashboard／HTML視覺、AI與雲端仍未驗收。`Verify-LiveFixture.ps1`每次建立新模型，結束清理自有程序；不接觸私人模型。


## Dashboard與動態HTML真機驗收（2026-10-13）

最新原始碼在全新自製PBIP通過34個正式檢查點：原13項基本流程、Dashboard預覽／SaveChanges回讀／分別授權查詢與匯出，以及三種動態HTML計畫的12個篩選情境。修正DAX FORMAT產生尾隨小數點、SVG不顯示的問題，增加SVG語法與有限數值檢查；423項回歸測試全部通過。4份實際HTML另經瀏覽器AX與畫面檢視，確認柱形、折線、期間交集與空資料呈現。

權威證據與歷次失敗保留方式見[Dashboard引擎驗收](dashboard-engine-acceptance.md)。TREATAS與本機瀏覽器不能代替Power BI自訂HTML視覺、切片器互動、原生Assistant操作、真實AI或雲端；安全token持久快取、公開remote／CI／release等原始缺口保留。此前的402項與13個檢查點為歷史紀錄。


## Windows雲端登入快取（2026-10-13）

原設計的可選安全token持久化已接入：預設關閉、DPAPI CurrentUser、client／tenant／account pin、30天絕對期限、MSAL通知中序列化及驗證後提交、獨占鎖／加密原子更新、改選帳號與登出／表單清除。一般設定不保存這項勾選、帳號或能力。32項新增測試通過；完整455項及12條WPF流程通過。實際Entra登入／silent refresh／帳號切換、跨Windows使用者及雲端服務仍待驗收。

[登入快取的使用、來源與驗收](cloud-login-cache.md)保留首次NameScope錯誤與修正證據；依賴報告更新為23個套件，分開10個桌面NuGet、1個framework提供與12個開發／測試相依。此前關於token持久化尚缺的描述屬前輪狀態。公開remote、CI／release與第三方二進位分發條件仍未完成。
