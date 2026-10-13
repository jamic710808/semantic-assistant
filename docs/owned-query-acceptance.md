# 自有 Desktop 查詢：結果大小與取消邊界驗收

日期：2026-10-13。固定小型 DAX、自有 inline 合成模型、正式 Desktop／ADOMD adapter；沒有修改、刷新、保存模型，沒有 AI 或私人模型存取。

## 本輪修正

1. 原 adapter 在加入資料列後才檢查大小；沒有列時，欄名、DAX、來源與期限欄位仍可能超出 MaxBytes。新增共用 `QueryResultBuffer`，空封裝與最後回傳物件都檢查完整 JSON UTF-8 位元組。大小探測與最終結果共用同一個 ID、來源、DAX 和取得時間；用較長的 `Truncated=false` 形式預留空間。Desktop 與雲端 parser 共用此實作。
2. 列數到達上限後仍需觀察是否有下一列，才能判定截斷；超過上限不讀取額外列的值。恰好達到上限且沒有下一列，保持 `Truncated=false`。
3. 本機 adapter 新增涵蓋整次呼叫的 linked cancellation deadline，不單靠 ADOMD 向上取整的 CommandTimeout 秒數。另以Stopwatch單調時間在回傳／例外出口重查期限，避免繁忙thread pool延遲timer回呼時接受過期結果或一般錯誤。取消後的 SDK／discovery 例外正規化為 `OperationCanceledException`；metadata 與 ADOMD 連線前後重查 token，不接受逾時後結果。

取消仍為 best effort。程序列舉、同步 TOM／ADOMD 連線與作業系統排程不保證在期限的精確毫秒返回；回傳結果的拒絕與實際引擎停止是不同證據。

## 實際驗收

完整 **528 passed / 0 failed / 0 skipped**；新增14項測試，包括共用 buffer、雲端 parser 的空封裝、直接 adapter 取消／期限，以及驗收器不把成功查詢當成預期拒絕。最終log：`artifacts/query-budget-monotonic-tests.log`；初輪本機／獨立解壓驗證紀錄亦保留。

首次source CI `38351818148` 在新期限測試捕捉到50ms故障注入結束時timer取消尚未發生，1ms期限呼叫回傳了IOException；不能把這種結果當作有效逾時拒絕。保留 `artifacts/query-budget-source-ci-failure.log`，加入上述單調時間重查；後續驗證以CI與獨立解壓來源的實際結果為準，沒有放寬期限或接受一般錯誤。

最終16個檢查點：
`artifacts/live-fixtures/SemanticAssistantSynthetic-dc58b078a0734af3b6c294572cb9b481/owned-query-verification-148eda1d51e8499f9f193078a2cc9e2b.json`。

| 固定檢查 | 結果 |
|---|---|
| 程序／父子身分與固定 inline 模型 | 通過；既有原生 Dashboard 完成後的固定10度量值形狀 |
| 5列查詢，限制2列 | 2列、截斷，589 bytes |
| 5列查詢，限制恰好5列 | 5列、不截斷，602 bytes |
| 完整封裝限制1024 bytes | 3列、截斷，962 bytes |
| Unicode 跳脫超出封裝預算 | 0列、截斷，498 bytes；沒有超限物件 |
| 單值1025字元、1500字元欄名、空結果含長DAX | 各自拒絕，沒有回傳結果 |
| 過期 revision | 拒絕 |
| 預先取消 | adapter inventory讀取0次，未提交查詢 |
| ADOMD連線後、ExecuteReader前取消 | 第6次inventory讀取觸發；未提交查詢，取消例外 |
| adapter自身100ms期限 | 在初始guard注入150ms延遲；拒絕，沒有提交DAX |
| 模擬inventory pin遺失 | 第1次讀取注入IOException，拒絕；實際程序未停止或替換 |
| 拒絕後新查詢／來源版本 | 正常取得合成700營收，模型結構未變 |

取消觸發點依目前 adapter 的兩次metadata EnsureCurrent（4次inventory讀取）與連線後一次EnsureCurrent（第5、6次）核對。第一次工具紀錄 `owned-query-verification-df7b0520a9b6468c846dedb7132fdedb.json` 用第4次取消，實際位於metadata末段；該紀錄的 `cancel_after_connection_guard` 名稱錯誤，不能作連線後取消證據。原檔保留，最終紀錄已改第6次，並在新增token檢查後重新驗證。

## 重跑

依[原生驗收入口](native-ui-acceptance.md)的自製模型程序紀錄，執行：

```powershell
dotnet run --project tests/SemanticAssistant.PowerBIFixture -c Release -- --verify-query-owned '<owned-desktop-session.json 完整路徑>'
```

只接受未加入物件的原始baseline，或[原生Dashboard](native-dashboard-acceptance.md)完成後的固定形狀。執行前核對官方exe、PID／啟動時間、引擎父程序、工作區與唯一catalog；pin不可用即拒絕，不自動選另一個實例。所有DAX都固定在工具內；不接受使用者DAX、目標catalog、資料列或憑證參數。每次新建GUID證據檔，沒有覆蓋舊紀錄。

報告刻意保留 `InFlightQueryCancellationVerified=false`、`ActualProcessRaceVerified=false`、`NativeAssistantUiVerified=false`。本輪沒有提交長時間運算後觀察取消，也沒有真實PID／port替換、故障修復、Power BI自訂HTML視覺、原生介面重測、真實AI或雲端驗收。這些仍是整體目標的缺口。
