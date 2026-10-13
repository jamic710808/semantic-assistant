# 原生介面（Native UI）獨立驗收

日期：2026-10-13。使用者安排稍後設定 AI 供應商；本次沒有讀取正式憑證或發送 AI 請求。

## 可執行入口

```powershell
.\scripts\Verify-LiveFixture.ps1 -KeepOpen -PrepareUi
# 上一步輸出新合成專案內 owned-desktop-session.json；填入它的完整路徑。
.\scripts\Start-NativeUiAcceptance.ps1 -OwnedSessionPath '<完整路徑>'
```

第一步建立全新的四表、69 筆 inline 資料 PBIP，核對官方 Desktop 執行檔、PID、啟動時間、父子程序、模型標記與全部固定資料來源，刷新後執行四條基準 DAX。`-PrepareUi` 在寫入前結束；成功且 `-KeepOpen` 才保留自有 Desktop。未帶此 flag 時既有完整引擎驗收照常執行。

第二步開啟相同產品 `App`／`MainWindow`；資料來源清單只包含該份 session 釘選的引擎，不會連線其他報表。設定、歷史、報告與 writer journal 使用新隨機目錄，沒有載入一般設定或恢復同意。所有實際操作仍需在原生控制項中進行，工具不會自動選來源、同意、寫入或宣告 UI 通過。

驗收宿主強制 WPF 軟體繪製（Software Rendering），因本機擷取環境的硬體繪製畫面為空白。此設定僅限驗收程序；一般產品硬體呈現尚未由本次證據確認。它不是限制所有按鈕或網路的安全沙箱。

## 本次實際操作與證據

自製專案：`artifacts/live-fixtures/SemanticAssistantSynthetic-dc58b078a0734af3b6c294572cb9b481/`。
Desktop 2.158.1177.0；自有 Desktop PID 16724、引擎 PID 34748、埠 59848。程序身分與啟動時間由 session 驗證，不依賴埠號單獨判斷。

| 實際原生操作 | 結果 |
|---|---|
| 初始狀態／來源選取取消 | 示範模型保留；未自動取得查詢或匯出同意 |
| 依序選 instance／catalog | 只選擇自有引擎；載入 4 表、13 欄、5 度量值、2 關係 |
| 搜尋 Revenue／點選度量值 | 顯示 `SUM('Sales'[Revenue amount])` 定義；未執行數值查詢 |
| 真實來源且無供應商 | 對話輸入與送出停用；可以本機檢視結構 |
| 勾選查詢／匯出後改報告模式 | 兩項勾選清除，舊回合與同意撤銷 |
| 混合 JSON 預覽／未同意按執行／關閉 | 明確拒絕；引擎唯讀檢查三個預定物件均不存在 |
| 新預覽／勾選單次同意／執行 | 三物件寫入成功；介面回讀成功、PartialFailure=false，同意清除 |
| 關閉 writer 返回主視窗 | 更新成 4 表、14 欄、6 度量值、3 關係 |

23 份可存取性觀察（Accessibility Observations）保留於 `artifacts/native-ui-observations-1791863323952/`。初始失敗／延遲觀察亦保留，通過判斷以後續有效畫面和引擎證據為準；不是每份檔案都代表通過。

未寫入證據：`owned-object-inspection-5f81f72dd49d4e5487b6e8f305291737.json`。
寫入後完整定義：`owned-object-inspection-ecc3c17487034e5abcc6ad683980f56a.json`。
操作 ID：`0e7e5a21-27e9-4226-9930-f06f02f34cec`；三物件皆 `SemanticAssistant/v1` 所有且完整性有效。

## 可重跑的引擎回讀

```powershell
dotnet run --project tests/SemanticAssistant.PowerBIFixture -c Release -- --verify-native-writer-owned '<owned-desktop-session.json 完整路徑>'
```

這條唯讀命令再次釘選自有程序，核對固定三物件定義、完整性、相同操作 ID，再以正式 ADOMD 執行兩條固定 DAX。它不修改或刷新模型，不把後端成功當作 UI 操作證明。

本次四個檢查點通過：`native-writer-readback-ff25d1cffc404795bc086cf38231e75e.json`。營收 700、利潤 280；新關係篩選為 North 380／South 320。此 JSON 的 `NativeAssistantUiVerified=false` 刻意保留，UI 證據由上述原生操作紀錄獨立提供。

## 已知失敗與未涵蓋項目

最初衍生 WPF App 載入資源失敗；改用產品原本的 App 並注入建窗函式。`startup-failure.json` 保留階段／例外型別／有限方法名稱，不保存原始伺服器錯誤。硬體繪製空白後改為驗收宿主 SoftwareOnly，未修改一般產品呈現設定。

本次涵蓋來源選取、模型檢視、模式撤銷與 writer。後續[原生 Dashboard 操作與引擎回讀](native-dashboard-acceptance.md)已完成主要流程；修正後介面重測被使用者停止而仍待驗收。AI 設定／外送同意表單與真實串流、原生查詢取消／PID 競態、引擎故障修復、雲端、Power BI 自訂 HTML 視覺與切片器仍待分別驗收。沒有操作私人報表，沒有宣稱與 ABI 所有功能等價。
