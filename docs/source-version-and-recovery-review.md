# 完整定義版本與重啟後稽核檢視

日期：2026-10-13。真實AI供應商仍待設定，本輪不登入雲端；使用者停止的原生介面操作未恢復。

## 來源版本修正

舊版Desktop／Cloud的 `ModelIdentity.Revision` 只計算表、欄、度量值與關係投影，因此M／SQL分割區、來源欄對應、角色篩選或其他未投影屬性改變，可能保留相同唯讀版本。writer雖已有完整定義版本，讀取／外送授權仍使用較弱的模型身分。

現在正式Desktop、Cloud及writer共用 `TomModelProjection.Read` 的完整TOM定義SHA-256：排除時間戳與SDK inferred objects／properties，保留8MiB完整定義及2MB投影上限。完整定義只在記憶體處理，`ModelSnapshot`仍只包含既有投影及雜湊，不外送分割區、連線字串、角色或認證內容。

既有來源／版本閘門因此能拒絕定義重綁後的舊查詢、上下文與寫入預覽；不是持久操作授權。修改自有物件同樣會改變版本，後續操作需要重新讀取與授權。離線TMDL reader既有整份檔案指紋保留。

11項新增TOM合成案例涵蓋M與SQL來源、來源欄、角色、資料來源、隱藏、格式、lineage、重複讀取／writer一致、大定義拒絕及已同意寫入的失效拒絕。既有projection測試改為確認owner annotation也使模型身分失效。

這是SDK可序列化定義版本；不是資料列內容／刷新世代、登入者有效RLS／權限、服務可用性或跨程序的原子鎖。忽略的inferred屬性、引擎內部狀態與SDK未提供的內容不能由此驗證。定義可在檢查與遠端命令之間改變，沒有宣稱compare-and-swap或外部模型交易。

## 故障後的唯讀檢視工具

CLI新增明確選取日誌目錄的入口：

```powershell
dotnet run --project src/SemanticAssistant.Cli -c Release -- --review-write-audit '<write-journal 完整目錄>'
```

正式桌面預設目錄為 `%LOCALAPPDATA%\SemanticAssistant\write-journal`；若使用另一個storageRoot，請選取該目錄的 `write-journal`。本輪只用新建的合成日誌檢查，不掃描使用者的實際目錄。

| 結果 | 意義 |
|---|---|
| recorded_verified | 日誌記錄完成回讀；不代表目前模型仍相同 |
| recorded_rolled_back | 日誌記錄當次還原回讀；不代表其他物件也被還原 |
| unresolved | intent或rollback_started之後沒有結案 |
| recorded_partial_failure | 日誌記錄部分失敗，須回讀模型比對操作ID |
| unreadable | 截斷、未知狀態、繫結不一致、格式損壞或不可讀，不能視為完成 |

退出碼0代表全部紀錄有合法結案（空目錄亦為0），3代表有待檢視紀錄，1代表整個目錄無法安全讀取。報告固定 `ModelStateVerified=false`、`ModelMutationPerformed=false`、`AuthorizationsRestored=false`。它不連線模型、不讀key、不保存同意、不自動重試、還原或刪除日誌。

單層最多1000檔，每檔最多20000 bytes／64筆JSON、嚴格UTF-8與完整換行；只接受v1固定欄位、相同操作／計畫／來源繫結及合法狀態序列。拒絕檔案／目錄重新解析點，檢查讀取時長度變動；這不是針對同權限本機攻擊者的原子檔案沙箱。原始錯誤、路徑、雜湊、名稱、表達式及列值不加入輸出。

20項新增稽核案例涵蓋合法／非法序列、截斷、繫結錯誤、重複／額外欄位、大小、UTF-8、取消、檔案數、實際合成writer日誌及讀取後仍禁止重用操作ID。日誌沒有模型備份也沒有防竄改簽章；即使顯示recorded_verified仍需以當前模型回讀確認。

## 復原邊界

本輪來源ZIP已獨立重建：585項測試、12條合成WPF流程通過。自建Desktop模型的16項唯讀查詢檢查亦通過，證據 `artifacts/live-fixtures/SemanticAssistantSynthetic-dc58b078a0734af3b6c294572cb9b481/owned-query-verification-e6fa038357b7466c99d8dbc9fb7b497d.json`；未寫入／刷新模型、未存取私人模型，不能據此宣稱實際來源重綁競態已驗收。

重建後CLI在新建合成日誌通過退出碼3（含未結案／損壞）與0（已結案）兩個案例；證據位於 `artifacts/audit-cli-b18e941b67bd4d8ebb87de11b0e8bce5/`。該次重建與最終來源只差文件文字，正式提交的CI與ZIP一致性另以驗收收據核對。

現有coordinator在當次失敗時，仍只嘗試還原本次自有且沒有被外部修改的物件，以獨立10秒repair token回讀驗證；失敗則顯示partial_failure。此工具改善重啟後的盤點，沒有補出跨重啟自動還原或真實引擎故障復原證據。下一階段需在獨立自建模型上注入實際提交後故障，驗證保存、回讀、外部修改保留與還原；不能以本輪離線測試代替。
