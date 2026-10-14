# Semantic Model Assistant：ABI Assistant 逆向與開源重建

目前本機桌面版本：**1.0.1（開發版）**，完整編號 `1.0.1-dev.20261015`。固定啟動位置為 `app`，上方「關於」顯示正在執行的版本及建置時間；詳 [版本與關於視窗](docs/about-version.md)。本機版本不代表 GitHub 已建立同名 release。

1.0.1 累積更新包含桌面介面、AI 模型設定保存與切換、報告名稱與啟動偵測、範本／配色、模型修改總開關、首頁快捷提問、AI 模型修改工具及 Dashboard 更新流程。對話支援 Markdown 排版與複製，工具執行細節移至側欄；送出時預設允許資料外送，不再逐回合跳授權視窗。詳 [預設權限](docs/full-access-defaults.md)、[對話排版](docs/readable-chat.md)、[模型工具](docs/model-tools-step2.md)。此版已有本機建置與部分介面檢視；尚未對全部累積變更重新執行回歸／實機驗收，亦未證明與 ABI 完全等價。

下列測試數字是先前來源版本的歷史證據，不代表 1.0.1 本次重新通過：

先前接續：[完整定義版本與重啟後稽核檢視](docs/source-version-and-recovery-review.md)。585項回歸及12條合成桌面流程通過；Desktop唯讀查詢16項實機檢查通過。新增 `--review-write-audit <journal-folder>` 檢視未結案／損壞紀錄；不恢復授權或自行還原。真實AI、雲端與引擎故障復原仍待驗收。

最新引擎驗收：[執行中的查詢取消與逾時](docs/owned-cancellation-acceptance.md)完成9個真實引擎檢查點；538項回歸通過。觀測活動命令、結果丟棄及後續無活躍標記分別記錄；引擎中止原因、實際程序競態與原生介面仍待對應證據。

前輪查詢補強：[完整結果預算、adapter自身期限與自有Desktop驗收](docs/owned-query-acceptance.md)。528項回歸、16個正式引擎／guard檢查點通過；當時命令提交前的取消證據與上方後續引擎驗收分開保留。

最新驗收：[原生 Dashboard、獨立同意、匯出與動態 HTML](docs/native-dashboard-acceptance.md)主要操作及七項引擎回讀通過；完整514項回歸與12條WPF流程通過。修正後的介面重測被使用者停止，仍保留待驗收；AI供應商依使用者安排稍後設定。[來源選取與writer](docs/native-ui-acceptance.md)、[保存／關閉／重開](docs/desktop-persistence-acceptance.md)的既有證據保留。

[![Windows CI](https://github.com/jamic710808/semantic-assistant/actions/workflows/build.yml/badge.svg)](https://github.com/jamic710808/semantic-assistant/actions/workflows/build.yml)

[來源預覽版下載與版本紀錄](https://github.com/jamic710808/semantic-assistant/releases)；[CI 建置與驗證紀錄](https://github.com/jamic710808/semantic-assistant/actions)。

狀態：M1–M6 所列主要流程已有程式碼與合成驗證；MCP Schema 隔離、情境建構器、查詢結果期限、可選設定保存、五種操作模式與可選歷史保存已加入。共用工具註冊、政策引擎與MCP明確匯入已加入；合成PBIP已在真實Desktop通過刷新、ADOMD查詢、三種TOM物件寫入、Dashboard及動態HTML篩選內容驗收；原始碼已公開。原生來源選取／取消、結構檢視、模式撤銷及writer已操作驗收；真實AI、雲端、Power BI自訂HTML視覺及其餘原生流程仍待驗收。
歷史證據日期：2026-10-13；本次開發版日期：2026-10-15。

這個專案記錄對 ABI Assistant 5.0.2 的靜態分析，並自行實作可由原始碼建置的 **Semantic Assistant**。可執行桌面示範與核心工具流程；自行重寫來源採 MIT，原始碼已公開於 [jamic710808/semantic-assistant](https://github.com/jamic710808/semantic-assistant)。目前是來源預覽版，真實 AI 與雲端服務仍待驗收。

開源準備：[LICENSE](LICENSE)、[NOTICE](NOTICE)、[24 個套件的版本與條款清單](docs/dependencies.json)、[來源封裝與驗證](docs/release.md)。第三方二進位保留各自條款；JsonSchema.Net 等預編譯套件附有維護費條件，來源 ZIP 沒有收錄第三方 DLL。CI 只上傳合成驗證紀錄與畫面，建置產生的 DLL 不列入下載附件。

```powershell
git clone https://github.com/jamic710808/semantic-assistant.git
Set-Location semantic-assistant
.\scripts\Start-Demo.ps1
```

Dashboard／動態HTML的獨立引擎驗收工具與三份計畫已加入；[操作、預期值與驗收界線](docs/dashboard-engine-acceptance.md)。修正SVG座標尾點後，全新模型通過34個真機檢查點、423項回歸測試及4份實際HTML的瀏覽器檢視；私人模型與Power BI自訂HTML視覺不在此證據範圍。

## 立即啟動

建置固定桌面版本後，雙擊根目錄的 `Start-Semantic-Assistant.cmd`（或中文啟動入口）：

```powershell
.\scripts\Publish-Desktop.ps1
.\Start-Semantic-Assistant.cmd
```

`app/` 為本機建置輸出，不提交 Git。啟動器使用相鄰工作目錄的 `.tools\dotnet10` 或系統 `dotnet`；其他電腦需安裝 .NET 10 SDK。

M3 真實 AI 驗收入口：[預檢、供應商設定與證據判定](docs/ai-provider-acceptance.md)。先執行 `scripts/Verify-AiProvider.ps1`；預設不發送 AI HTTP、不讀 key 或模型。真實供應商需另填 profile 並明確開啟 live／外送 flags，未執行時保留待驗收狀態。

雲端登入新增預設關閉的30天加密快取、改選帳號及登出清除；[使用與驗收界線](docs/cloud-login-cache.md)。本機DPAPI／MSAL快取通知及登入表單已驗證，真實OAuth與Power BI雲端服務另行驗收。

獨立 Power BI 驗收專案：執行 `scripts/New-LiveFixture.ps1`，產生含69筆合成資料的新PBIP、四條DAX、預期值及writer／Dashboard計畫。[測試模型與真機驗收步驟](docs/live-fixture-acceptance.md)。產生器只寫檔；另有 `scripts/Verify-LiveFixture.ps1`，每次建立新專案、啟動獨立Desktop、核對程序身分與固定來源、完成查詢／寫入後清理自有程序。

也可直接雙擊專案根目錄的 `Start-Demo.cmd`。

Windows + .NET SDK **10.0.401**。本機 SDK 位於 `D:\KEN\codex\.tools\dotnet10`，啟動腳本會優先使用它；沒有改系統 PATH。其他電腦需自行安裝對應 SDK。[Microsoft 安裝說明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

```powershell
Set-Location 'D:\KEN\codex\abi-assistant-open-architecture'
.\scripts\Start-Demo.ps1
```

桌面操作：

1. 送出「介紹模型結構」，或點選首頁三個快捷提問。
2. 「引導式提問」設定本地／雲端各三個按鈕的文字，保存後同步首頁。
3. 送出「分析合成數值」可查看合成結果；來源支援時，查詢能力預設啟用。
4. 本機 HTML 匯出預設允許；檔案寫到 `%LOCALAPPDATA%\SemanticAssistant\reports`。
5. 執行中可以取消；這同時撤銷當次授權，結果不會保留為可匯出報告。
6. 查詢結果仍受有效期限約束；過期後須重新查詢。Dashboard 匯出與固定 HTML 快照使用相同期限；[既有期限契約](docs/query-result-retention.md)。權限預設與遷移以 [1.0.1 說明](docs/full-access-defaults.md) 為準。

模式選單提供「結構與分析」「報告模式」「HTML 檔案」「HTML 度量值」「Dashboard」。模式切換清除舊回合狀態，再依來源及保存設定套用能力。[既有路由契約](docs/mode-controller.md)、[新版對話流程](docs/conversation-step1.md)。

歷史試用：點選「本機歷史與保存政策」，分別勾選回合摘要／報告索引並保存政策；之後完成的回合及已同意匯出的HTML才會新增紀錄。預設關閉，最長30天／最多100筆；移除索引或清除歷史保留報告檔案，不恢復查詢或同意。[保存內容與限制](docs/history-store.md)。

離線模型檢視：

1. 点選「載入內附範例」即可試用，不需要準備 Power BI 檔案。
2. 或點選「開啟 TMDL 資料夾」，明確選取 PBIP 語意模型的 `definition` 目錄。此功能不接受 PBIX，也不會連線 Power BI Desktop。
3. 左側列出表、欄位、度量值與單欄關係；搜尋框比對物件名稱。
4. 選取物件，中央「模型詳情」顯示欄位型別、度量值 DAX 定義或關係兩端；可選取文字複製，沒有執行 DAX。
5. 按「重新讀取」更新檔案內容；讀取失敗、取消或超時會保留原本模型。
6. 「返回示範」恢復合成模型。切換來源會清除先前對話、查詢結果與授權。離線來源不提供數值查詢；設定 AI 後可另行授權結構分析。

AI 與本機 Desktop：

1. 「設定 AI 供應商」填完整 `/chat/completions` HTTPS 端點與供應商支援的模型名稱。設定不會發送請求；本機模型可使用 loopback HTTP。
2. 「AI 配置」可新增、保存、修改、刪除多個模型設定並切換目前模型。API key 儲存於 Windows 認證管理員，設定文件保存端點、模型名稱與設定識別碼。[模型設定管理](docs/ai-model-profiles.md)。
3. 送出對話預設允許訊息、模型結構／DAX、查詢結果及已匯入工具資訊外送至已設定供應商，不顯示逐回合授權視窗。
4. 「連接本地報告」列舉執行中的 Power BI Desktop 報告，選取後讀取結構；啟動時可自動開啟選擇視窗。查詢能力依來源支援啟用，沒有在連線時自動查詢所有資料列。[報告偵測](docs/desktop-report-discovery.md)。
5. 埠、PID、啟動時間或模型結構變更時，舊來源被拒絕。按「重新讀取」或重新選取報告以取得新來源。
6. 「使用本機模擬 AI」切回示範供應商；真實／離線來源此時只提供本機結構檢視。
7. HTML 匯出可選表格、KPI＋長條圖或 KPI＋折線圖。圖表數值只取自當次成功查詢結果；截斷結果的合計不代表完整母體。
8. 「本機執行預算」可調低摘要、查詢與AI回合限制，保存後在下一輪生效；Dashboard也使用保存的查詢限制。設定保存於 `%LOCALAPPDATA%\SemanticAssistant\preferences\settings.json`，包含模型修改總開關、AI 設定與介面偏好，不包含 API key 或查詢資料列。[既有保存契約](docs/settings-store.md)、[權限設定與遷移](docs/full-access-defaults.md)。

詳細契約與限制：[M3 AI 串流與授權](docs/ai-streaming-egress.md)、[本機適配器](docs/local-desktop-adapter.md)、[M4 報告規格](docs/report-spec.md)。

內附檔案位於 `examples/synthetic-model/definition/`，由本專案自行建立，會一起封裝到桌面輸出目錄的 `Examples/definition/`。

CLI 與驗證：

```powershell
.\scripts\Start-Demo.ps1 -Cli
.\scripts\Start-Demo.ps1 -Cli -AllowQuery
.\scripts\Verify.ps1
```

封裝後驗證（八條合成登入快取／設定重載／MCP匯入／歷史保存／模式控制／Dashboard／AI 提案／MCP 隔離流程，沒有連接 Power BI）：

```powershell
& 'D:\KEN\codex\.tools\dotnet10\dotnet.exe' publish src\SemanticAssistant.Desktop -c Release --no-restore -o artifacts\desktop
.\scripts\Verify-Package.ps1
```

CLI 預設輸出合成流程的 JSON events；沒有查詢 rows。`--inspect-tmdl` 是使用者明確指定的本機 metadata 輸出，不送 AI：

```powershell
& 'D:\KEN\codex\.tools\dotnet10\dotnet.exe' run --project src\SemanticAssistant.Cli -- --inspect-tmdl '你的PBIP語意模型\definition'
```

此命令須選擇 **TMDL definition 目錄**，不接受 PBIX binary，也不會連線 Power BI Desktop。實際 PBIP 相容性尚需使用者選取測試檔案驗收；本輪僅用自行建立的合成 TMDL。

## 已實作原始碼

| 專案 | 責任 |
|---|---|
| `src/SemanticAssistant.Contracts` | 不可變模型／工具／查詢 DTO、版本化事件 |
| `src/SemanticAssistant.Application` | 回合、工具參數驗證、授權、取消、限制、HTML render/export、模型來源切換與結構檢視 |
| `src/SemanticAssistant.Adapters.Demo` | 自製合成模型及 deterministic AI provider |
| `src/SemanticAssistant.Adapters.AI` | OpenAI-compatible HTTP、SSE 解析、完整工具組裝、外送 lease 檢查 |
| `src/SemanticAssistant.Desktop` | 原生 WPF 桌面示範、TMDL 目錄選取、搜尋、物件詳情與重新讀取 |
| `src/SemanticAssistant.Cli` | 無 UI 示範、明確指定 TMDL metadata inspection、獨立 MCP Schema worker |
| `src/SemanticAssistant.Adapters.PowerBI.Files` | 官方 TOM parser 的離線 metadata reader；無連線／query／writer |
| `src/SemanticAssistant.Adapters.PowerBI.Local` | Desktop 埠／PID 列舉與版本檢查、TOM 結構與受限 ADOMD DAX 查詢 |
| `src/SemanticAssistant.Adapters.PowerBI.Cloud` | MSAL 使用者登入、REST 工作區／資料集／DAX、XMLA metadata；真實服務待驗收 |
| `src/SemanticAssistant.Adapters.Mcp` | 明確程序設定、stdio 工具清單／schema、隔離驗證、逐次同意與取消／關閉 |
| `tests/SemanticAssistant.FixtureHost` | 實際啟動的自製 MCP 合成子程序 |
| `tests/SemanticAssistant.Application.Tests` | 授權、契約、上限、取消、報告與合成 TMDL 測試 |

Solution：`SemanticAssistant.slnx`。套件版本與 transitive resolution 記錄在各專案 `packages.lock.json`；`Verify.ps1` 用 locked-mode restore。`.github/workflows/build.yml` 在 push／pull request 時執行 Windows CI；本次狀態以 GitHub Actions 紀錄為準。

外部 AI、Desktop 與雲端適配器已有程式碼與合成測試；真實 provider、使用者模型、OAuth／REST／XMLA 與 PBIP 相容性尚未驗收。雲端、MCP、模型修改及 Dashboard 入口位於「更多來源與工具」。Demo DAX runner 僅接受固定示範查詢與一份已知合成 Dashboard 計畫，不是通用 DAX 引擎。使用原生 WPF；React/WebView2 與其信任來源機制尚未實作。

Dashboard 試用：保持合成模型 →「Dashboard 計畫」→ 檢查計畫 → 確認差異並勾選當次寫入 → 另行勾選查詢 → 另行勾選匯出。三個授權獨立、完成後清除；取消／來源切換會使舊授權失效。數值為三列合成月份，合計397000。固定 HTML 保存查詢快照；動態 HTML 在指標驗證成功後可直接產生 DAX，另開修改預覽與同意，需支援 HTML／SVG 的視覺。示範只保存DAX，不執行它。

AI 提案試用：保持數值查詢關閉 → 送出「建立合成Dashboard提案」→ 右側「檢視AI Dashboard提案」→ 檢視JSON與DAX → 檢查計畫並預覽 → 逐次同意。AI工具只驗證計畫，不自動查詢或寫入；外部AI需另授權結構外送。[動態HTML／提案契約與限制](docs/dynamic-html-dashboard-proposals.md)。

MCP供AI使用：先選來源／分析或報告模式及配置AI，再啟動所選程序、查看工具並明確匯入；勾選當次AI工具使用與獨立外送類別。AI提出操作後仍展示完整參數、逐次同意才執行；回合結束清除啟用，來源／模式變更清除匯入。[工具政策與完整方法](docs/tool-registry-policy.md)。

詳見 [MCP 操作與限制](docs/mcp-stdio.md)、[模型修改與 Dashboard](docs/model-writing-dashboard.md)、[雲端唯讀](docs/cloud-readonly.md)。

模型物件試用：合成模型 →「模型修改預覽」→「載入合成混合範例」→ 核對計算欄位型別、關係兩端／方向與修改差異 → 勾選當次同意。成功後左側模型樹會顯示新欄位與未啟用的關係。這只改自製記憶體模型，不計算新欄位資料；[完整格式與限制](docs/model-object-writing.md)。

## 本階段結論

- 目標程式是 .NET 6、WPF 與 WebView2 桌面應用；前端訊息與圖表呈現由本機 HTML/JavaScript 頁面負責。
- 可辨識的主要運作鏈是：模型連線與中繼資料整理 → AI 串流工具迴圈 → 桌面端檢查並執行工具 → 將結果回傳 AI → 報告／HTML／Dashboard 呈現。
- 它同時具備唯讀查詢與模型修改工具；修改能力由設定閘門控制，Dashboard 更新還有回讀驗證及失敗清理／還原的設計。
- Core 組件含 Confuser 相關標記，離線反編譯時大量方法本體呈現無效 IL。文件因此分開記錄可觀察事實、合理推論與未知，不把工具名稱當成內部演算法證明。
- 這次只分析安裝檔、組件中繼資料及 UI 靜態程式碼；沒有連線或查詢你的 Power BI 模型，也沒有讀取使用者設定或憑證。

## 文件

- [原始架構覆蓋與未完成條件](docs/scope-audit.md)
- [MCP Schema 隔離與資源限制](docs/mcp-schema-isolation.md)
- [情境選擇與摘要預算](docs/context-builder.md)
- [查詢結果期限與清除](docs/query-result-retention.md)
- [本機設定保存與預算](docs/settings-store.md)
- [五種操作模式與撤銷](docs/mode-controller.md)
- [可選歷史、期限與清除](docs/history-store.md)
- [共用工具政策與MCP明確匯入](docs/tool-registry-policy.md)
- [逆向分析與證據](docs/reverse-analysis.md)
- [開源替代架構](docs/open-architecture.md)
- [分階段實作與驗收](docs/roadmap.md)
- [設計審查、修正與 ADR](docs/design-review-m1.md)
- [M1 契約與錯誤碼](docs/m1-contracts.md)
- [本機驗證紀錄](docs/verification.md)
- [離線桌面模型檢視](docs/offline-model-viewer.md)
- [MCP stdio 操作與限制](docs/mcp-stdio.md)
- [模型修改、Dashboard 與 HTML 度量值](docs/model-writing-dashboard.md)
- [計算欄位、關係與混合批次](docs/model-object-writing.md)
- [動態 HTML 與 AI Dashboard 提案](docs/dynamic-html-dashboard-proposals.md)
- [實作覆蓋與接續審查](docs/implementation-review.md)

## 自行重寫與公開發佈界線

新專案應自行撰寫程式、提示詞、介面文字、模板與測試資料；不把原廠反編譯程式碼、圖示、品牌素材或安裝檔複製進公開 repository。新程式的版本、建置與發佈由自己的來源庫和 CI 管理，不依賴 ABI Assistant 的安裝器或更新服務。

同一分析者已看過部分反編譯內容，因此不能把此流程稱為嚴格隔離的 clean-room。自行重寫來源採 MIT；依賴清單已加入，第三方二進位分發審查與實際公開發布仍待完成。原逆向暫存檔未複製進此專案。
