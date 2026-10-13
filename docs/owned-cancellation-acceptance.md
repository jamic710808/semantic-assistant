# 自有 Desktop：執行中的查詢取消與逾時驗收

日期：2026-10-13。這是引擎驗收工具，沒有操作使用者停止的原生介面，也沒有 AI／雲端或私人模型存取。

## 觀測方法與判定

正式 `DesktopModelAdapter` 執行帶有隨機 GUID 欄名的固定 DAX：500萬個合成整數的有限數學計算，只回傳一個值，不讀取業務資料列。兩個案例分別採10秒查詢期限加呼叫端取消、2秒 adapter 自身期限；外層45秒發出取消要求。程序／工作區／唯一catalog及固定inline模型必須先通過驗證，不選取其他引擎。

獨立 ADOMD 連線使用動態管理檢視（Dynamic Management Views, DMV）的 `DISCOVER_COMMANDS`。欄位依[Microsoft 命令列集定義](https://learn.microsoft.com/en-us/openspecs/sql_server_protocols/ms-ssas/c36a7837-027d-49e0-9df8-bb1f5246a7c4)核對：Session SPID、開始／結束UTC時間、已執行毫秒及命令文字。它是某個時間點的狀態快照，[不能當作逐事件即時追蹤](https://learn.microsoft.com/en-us/analysis-services/instances/use-dynamic-management-views-dmvs-to-monitor-analysis-services?view=sql-analysis-services-2025)。

判定要求：

- 同一隨機標記的命令，SPID有效、開始時間在本次提交後、結束時間為空／未初始化。
- 呼叫端案例在取得快照且adapter task尚未完成時觸發取消；adapter回傳取消例外，不交付結果。
- adapter期限案例要求命令在期限前開始；收到快照時task可以已因期限取消。用同一Windows主機的UTC開始時間與命令已執行毫秒核對觀測窗口，另記錄收到快照是否已過期限。
- 結束後再觀測，沒有仍活躍的相同標記；新查詢可以正常取得合成700營收，metadata revision未變。

命令文字只在記憶體識別標記，不保存DMV原始列／命令／使用者名稱。診斷查詢最多128列、每個文字256000字元、文字UTF-8累計2MB；標記命令仍限8192字元，每次DMV命令timeout2秒。這是自有引擎的診斷預算，不改變產品QueryResult的1024–64000 bytes限制。

## 本輪結果

完整 **538 passed / 0 failed / 0 skipped**，新增10項標記與觀測窗口測試；`artifacts/cancellation-all-tests.log`。

最終9個引擎檢查點通過：
`artifacts/live-fixtures/SemanticAssistantSynthetic-dc58b078a0734af3b6c294572cb9b481/owned-cancellation-344c28e9e6694cd7a7ce805a73a97d82.json`。

| 案例 | 實際觀察 |
|---|---|
| 呼叫端取消 | 命令已執行25ms、End=null；約1767ms收到快照並要求取消，約2185ms返回取消、沒有結果；再觀測沒有活躍標記 |
| adapter自身2秒期限 | 命令在本次提交後約1201ms開始，取樣時已執行770ms、End=null；約2336ms才收到快照，約2653ms返回取消、沒有結果；再觀測沒有活躍標記 |
| 恢復檢查 | 合成營收700，metadata revision一致 |

報告的 `InFlightQueryCancellationVerified` 代表「觀察到真實命令正在執行後取消呼叫，結果丟棄，隨後無活躍標記」；`AdapterInFlightDeadlineVerified` 是第二案例的相應判定。SDK取消要求、伺服器接收、中止運算及狀態快照有傳遞延遲，不能把2秒配置當成精準2秒返回。

`EngineAbortCauseVerified=false`、`ServerReportedCancellationReason=false` 仍保留。這些DMV觀測不能獨立證明引擎是因Cancel RPC、CommandTimeout或連線結束而停止，也不證明精準中止瞬間、所有工作執行緒已停止、原生按鈕／介面響應、真實PID競態、雲端或AI服務。

## 首次失敗保留

- `owned-cancellation-b36fc923dce647c589cab4525ada662d.json`、`...67e054b851db44ab9c65bb1bea182904.json`：先前TOM命令文字超過最初8192字元診斷上限。改用有界且分離的診斷預算，產品結果上限保留。
- `...a7cef187aeae41579c7ed7eab22de40a.json`：呼叫端三個檢查已通過；2秒期限快照抵達時task已取消，原驗收器錯把延遲抵達當成未觀察到活動命令。
- UTC窗口修正前的紀錄由 `artifacts/cancellation-clock-window-live-run.log` 指向。最終判定保留UTC窗口與分別成功旗標；沒有用普通例外或正常結果代替取消例外。

這些失敗均由各次CLI正常退出後才接續診斷；沒有因觀測逾時而另啟動重複查詢。

## 重跑

沿用[自有查詢驗收入口](owned-query-acceptance.md)的固定baseline或原生Dashboard完成後模型形狀：

```powershell
dotnet run --project tests/SemanticAssistant.PowerBIFixture -c Release -- --verify-cancellation-owned '<owned-desktop-session.json 完整路徑>'
```

每次建立新的GUID證據檔，不覆蓋舊檔、不保存操作同意、不刷新或寫入模型。沒有原生操作、私人報表或真實AI供應商依賴。
