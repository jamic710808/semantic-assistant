# 原生 Dashboard：操作、引擎與產物驗收

日期：2026-10-13。原生操作使用來源提交 `25a86875ef17152edd108788a11df97381194f71` 的產品視窗，軟體繪製、自有 Desktop 引擎與隔離儲存目錄。使用者安排稍後設定 AI；本流程沒有 AI 外送或私人模型存取。

## 實際操作結果

| 原生操作 | 結果 |
|---|---|
| Dashboard 模式開啟計畫 | 無 AI 供應商亦可操作；初始寫入、查詢、匯出同意均關閉 |
| 輸入合成計畫並預覽 | 顯示三個新指標、目標版本、操作 ID、計畫雜湊及正式 DAX；沒有自動寫入或查詢 |
| 未勾寫入同意按執行 | 拒絕；獨立引擎檢查確認仍只有先前 writer 的三物件 |
| 勾選同意後編輯計畫 | 原同意清除，舊計畫不能執行；首次操作發現舊預覽文字仍顯示，修正如下 |
| 新預覽、單次同意、三指標寫入 | 回讀成功、PartialFailure=false，寫入同意清除 |
| 未勾查詢同意按查詢 | 拒絕；寫入同意不能代替數值查詢同意 |
| 另行勾選查詢並執行 | 2 列、未截斷、5 分鐘暫存期限；查詢同意清除，匯出同意仍關閉 |
| 未勾匯出同意按匯出 | 拒絕；查詢同意不能代替檔案匯出同意 |
| 另行勾選匯出並執行 | 產生隔離目錄下的 HTML；匯出同意清除 |
| 固定快照子視窗 | 原同意不繼承；完整預覽後關閉而未寫入 `SA_DashboardHTML` |
| 動態 HTML 子視窗 | 新預覽及獨立同意後寫入 `SA_Fixture HTML default`，回讀成功，單次同意清除 |
| 子寫入結束返回父視窗 | 舊計畫、結果及三種同意撤銷；主模型樹更新成 4 表、14 欄、10 度量值、3 關係 |

自有專案：`artifacts/live-fixtures/SemanticAssistantSynthetic-dc58b078a0734af3b6c294572cb9b481/`。
25 份觀察保留於 `artifacts/native-dashboard-observations-1791865739642/`。
Dashboard 指標操作 ID：`8fc6e6ce-c61d-403a-bb13-0db67a9ee17a`。
動態 HTML 操作 ID：`1efeccb4-afc6-47c5-9e74-1db3af0fc2e9`；沒有沿用指標寫入操作 ID。

未授權後的唯讀證據：`native-writer-readback-c5a61a6ced3447c8b826efad115a318d.json`。
完成後的七個唯讀檢查點：`native-dashboard-readback-4b793287c401469f96cac5e8def61a8c.json`。

## 正式引擎與瀏覽器結果

| 月份 | 營收 | 成本 | 利潤 |
|---|---:|---:|---:|
| 2026-01 | 330 | 200 | 130 |
| 2026-02 | 370 | 220 | 150 |
| 合計 | 700 | 420 | 280 |

動態 HTML 另通過 All／North／South／Missing 四種正式 DAX 篩選；North 營收 380、利潤 150，South 營收 320、利潤 130，Missing 顯示空資料。HTML／SVG 的表格、KPI、圖形數量與座標以 bounded XML 檢查，不解析 DTD 或外部實體。

實際匯出：`native-ui-a2ace44f810c4cf5ad48de78d393ee86/reports/report-20261013-043851-c168c8ae15be4cd4a6684e19b60b4123.html`。
瀏覽器實際確認兩個月份的數值、700／280 KPI 與兩根長條。預覽服務僅在 loopback 提供這一份自製報告，驗收後關閉；首次分頁失效後以新分頁完成檢視。沒有藉由來源程式碼推定圖表已顯示。

## 重跑入口

先依[原生介面入口](native-ui-acceptance.md)建立全新合成模型、開啟隔離視窗並選來源。使用該新專案 `plans/model-changes.json` 完成固定 writer 三物件，再在 Dashboard 輸入 `plans/dashboard-plan.json`，依上表分別預覽及同意。動態 HTML 名稱填 `SA_Fixture HTML default`；固定快照只預覽後關閉。

```powershell
dotnet run --project tests/SemanticAssistant.PowerBIFixture -c Release -- --verify-native-dashboard-owned '<owned-desktop-session.json 完整路徑>'
```

這條命令只回讀及查詢：核對執行檔、PID／啟動時間、父子程序、固定 inline 來源、完整定義、所有權、完整性、三指標相同操作及 HTML 獨立操作，再執行月份及四種 HTML 查詢。未達固定形狀或來源條件即拒絕，不選取其他引擎。CLI 證據刻意保留 `NativeAssistantUiVerified=false`；原生操作證明來自上述觀察，兩者不能互相替代。

## 本次修正與停止後狀態

原生操作發現：編輯計畫或完成子寫入後，核心已撤銷計畫與同意，但畫面仍保留舊預覽。`ResetPlan` 現在清空預覽文字並提示重新檢查及逐項同意；已匯出的實體 HTML 保留。WPF 合成流程增加實際 TextChanged 事件檢查，以及子寫入後預覽必須清空的斷言。

新增十項固定 Dashboard 定義回歸測試，拒絕指標／HTML 篡改、不同指標操作、HTML 沿用父操作、所有權／完整性失效、資料來源變更、額外及缺少物件。完整 **514 項測試、12 條 WPF 流程通過**，log：`artifacts/verification-native-dashboard.log`。

修正後的原生介面重測已啟動，但使用者以 Esc 停止 Computer Use；沒有完成該重測，不把啟動紀錄當作通過。既有原生 Dashboard 主要流程證據保留，修正的事件及畫面狀態由 WPF 合成流程驗證，人工／原生重測仍待使用者恢復。

未涵蓋：固定 HTML 快照實際寫入、任意 Dashboard 計畫、Power BI 自訂 HTML 視覺／切片器、原生查詢取消／競態、引擎故障修復、硬體呈現、真實 AI／雲端。整體目標仍未完成。
