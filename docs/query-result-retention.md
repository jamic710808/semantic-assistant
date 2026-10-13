# 查詢結果暫存期限（Time to Live, TTL）

日期：2026-10-13。暫存最長5分鐘，獨立於外送授權、寫入approval與模型revision。

## 生命週期與核心邊界

- `QueryResult` 建立時取得唯讀 `ReceivedAt`、`ExpiresAt` 及程序內單調時間戳；`with` 複製保留原期限，不因顯示、匯出或再授權而續期。單調時間戳不序列化。
- `QueryResultRetention` 同時取UTC截止與單調耗時，使用較短剩餘期限。UTC往回跳不能延長至超過5分鐘，UTC往前跳可提早過期；負單調耗時拒絕。測試替代時鐘只由可信宿主提供，不由AI或外部工具選擇。
- `ToolDispatcher`／`DashboardCoordinator` 在回傳已授權結果前驗證；過期結果不回傳數值列。
- `ConversationCoordinator` 在下一輪、串流事件與最終成功前驗證；失效時停止後續工具／文字交付、清除outcome中的query results，回傳 `result_expired`。
- `OpenAiCompatibleProvider` 在序列化工具結果及credential讀取完成、送出HTTP前驗證；過期資料不開始新請求。外送lease仍另行檢查。
- renderer在開始與完成時檢查；exporter在渲染、建立輸出前與I/O完成後檢查。過期不回傳成功路徑，開始前過期不建立資料夾。I/O已開始後才過期或撤銷，檔案可能已寫入，不能將失敗當作完全沒有產物。

## 桌面與Dashboard

主視窗與Dashboard每秒排程清理過期結果；真正清除依UI執行緒下一個可排程tick，不保證即時畫面擦除。每次操作仍另作期限檢查。

- 主視窗釋放latestResult、清除結果區、查詢／匯出勾選，撤銷DataQuery／ArtifactWrite；需重新同意查詢。
- Dashboard釋放result／ReportSpec、清除匯出勾選、停用匯出與固定快照；已驗證指標可另行查詢，動態DAX模板沒有引用暫存資料列。
- 固定HTML的子寫入視窗綁定原QueryResult。過期清除JSON／差異／當次同意，撤銷能力並取消進行中的操作；預覽／apply的deadline另受來源剩餘時間限制。原生SaveChanges與修復仍遵守既有取消／回讀／partial failure契約，不能宣稱OS能硬性中斷原生寫入。
- 關閉視窗停止timer並釋放相關引用；來源切換與取消的既有授權失效規則保留。

## 資料保留的實際範圍

這是應用程式對暫存QueryResult的**使用期限與引用釋放**，不是加密抹除／GC立刻清零。其他可信程式碼若自行留存引用，已送到AI的內容、使用者複製內容、對話中已顯示的文字，以及已明確同意保存的HTML／模型物件，不能由這個TTL回收。沒有自動刪除先前合法匯出的產物，也不把已寫入模型的指標當作暫存列。

不從磁碟載入或重用序列化查詢列；getter-only時間欄位不是可匯入QueryResult的授權憑證。一般外部MCP文字結果不會被轉成QueryResult；AI匯入工具結果另有相同5分鐘的UTC／monotonic期限、複製不續期與HTTP邊界檢查，詳見tool-registry-policy.md。手動文字顯示與已送內容不屬可回收QueryResult。

## 合成證據

`QueryResultRetentionTests` 驗證唯讀／複製期限、UTC與monotonic變動、renderer／HTML快照／匯出／adapter／Dashboard拒絕、回合停止，以及外部provider在payload或credential階段過期時HTTP次數為0。測試不變更OS時鐘。

主視窗WPF smoke以相同清理函式與合成時鐘驗證資料區／勾選清除，再重新查詢成功。Dashboard smoke另驗證匯出／快照停用、重新查詢、子寫入預覽過期清除與無Attempted寫入。沒有使用真實Power BI、AI key或私人資料。
