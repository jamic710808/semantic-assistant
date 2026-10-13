# 雲端查詢期限：離線驗收

日期：2026-10-13。這次不登入帳號、不讀取使用者token、不連線XMLA／REST服務；真實AI供應商仍依使用者安排稍後設定。

## 修正與理由

`CloudModelAdapter.ExecuteAsync` 原本只用 `CancelAfter`。忙碌執行緒池可能延後取消回呼，非合作依賴也可能忽略token；方法結束前沒有額外期限檢查。

正式adapter現在使用內部 `CloudQueryExecution`，傳入原有XMLA結構讀取及REST執行方法。以系統單調時鐘（Monotonic Clock）計算整段期限，在開始、第一次結構回讀後、REST回應後、結果解析後、第二次結構回讀後檢查；超時或呼叫端取消時丟棄結果，將晚到的普通例外歸為取消。期限前的普通錯誤仍保留原判定。查詢前後的來源／結構版本核對保留。

REST層另在讀取認證前、讀取後、HTTP回應後及JSON解析前檢查取消，避免已取消的token仍取得認證或接受回應。沒有新增重試、登入、HTTP端點或使用者設定。

## 離線證據

14項新增pipeline案例使用自有合成結構及JSON回應：

- 時鐘單獨跨越期限、實際取消計時器尚未觸發時，三個依賴階段的晚到結果／普通錯誤均被拒絕。
- 呼叫前已取消：結構讀取及REST次數均為零。
- 依賴忽略呼叫端取消：三個階段均不能交付結果。
- 查詢前／後結構版本變動：拒絕；正常結果須兩次回讀，JSON文件完成後釋放。
- 期限前的普通錯誤不能被誤判成取消。

另有2項REST案例使用自有合成token與注入HTTP handler：呼叫前取消不讀認證、取得認證時取消不送HTTP。這16項均是離線證據；不代表真實雲端服務已通過。可執行完整來源驗收 `scripts/Verify-Release.ps1 -BuildExtracted`，或篩選 `CloudQueryDeadlineTests`／`CloudAdapterTests`。

## 邊界與下階段

這項修正保證方法觀察到完成後拒絕逾時結果，不保證非合作SDK立即返回、HTTP未曾送達、伺服器停止運算或精準deadline時刻的介面響應。同步XMLA／不合作依賴可能晚於配置期限返回，已送出的請求不能收回。實際Entra身分、token刷新、租用戶／Build權限、REST／XMLA與服務取消仍須明確設定後驗收。

自有Desktop的實際執行中取消另見 [引擎取消驗收](owned-cancellation-acceptance.md)；完整產品完成條件保留於 [覆蓋核對](scope-audit.md)。
