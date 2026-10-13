# 本機設定保存（Settings Store）

日期：2026-10-13。

## 操作與保存內容

AI設定中的「記住這個端點與模型」預設不勾選；已保存profile重載後顯示原選擇。勾選並套用才保存端點／模型，未勾選則只留在當次視窗。API key仍由既有Windows vault保存，這個選擇只控制profile，不刪除已保存的key。

「本機執行預算」保存摘要表數／字元、工具結果列數／UTF8 bytes、查詢期限、回合／工具數、輸出字元與整輪期限。上限與既有預設相同，只可調低；MainWindow將預算接到ContextBuilder、ToolDispatcher與ConversationCoordinator，Dashboard亦使用查詢列／bytes／期限限制。本機查詢最多10秒，雲端最多30秒。

路徑固定為宿主自己的 `%LOCALAPPDATA%\SemanticAssistant\preferences\settings.json`。保存端點／模型與預算為明文JSON；key不在其中。新視窗仍從合成模型開始，來源、session、capabilities、外送與寫入同意、列值、DAX、token與MCP環境不保存，不因重載profile自動讀key或發送HTTP。切換本機模擬AI會移除保存的profile，保留預算。

設定套用會清除舊回合／授權。保存失敗時可繼續使用本次視窗設定，介面會說明沒有保存成功；原檔保留。結果TTL仍固定5分鐘。

## 版本與拒絕規則

- `AssistantSettings` v2：必要Version、AiProvider（可為null）、Budgets，所有預算欄位必要。拒絕未知／重複欄位、缺欄、錯型別與超限數值。
- v1為本專案明確定義的舊格式，只含Version／AiProvider；讀入時補預設預算並正規化profile，**只在記憶體遷移，不自動重寫原檔**。下一次使用者明確保存才寫v2；不是ABI原廠設定的匯入器。
- 未知版本、無效JSON／UTF8／schema、超過16000 bytes、讀取失敗時回到預設示範並保留原檔。Save拒絕覆寫unknown／invalid／unavailable檔；修復此專案自己的設定檔後才可再次保存。
- profile沿用實際AI provider共用驗證：HTTPS完整chat/completions端點，loopback可HTTP；拒絕userinfo、query、fragment、控制字元與過長欄位。端點原文與正規化後均限2048字元，不接受query／userinfo夾帶key。

## 檔案I/O邊界

不接受相對／UNC目錄，檢查父路徑與檔案的link／reparse point。這是路徑檢查，不是對同使用者惡意程序的檔案沙箱，也不保證消除所有TOCTOU競態。

先以CreateNew建立本目錄隨機tmp、寫入並Flush(true)，再在同一目錄替換settings.json；失敗清理只限本次成功建立的tmp。程序內lock序列化操作，沒有跨程序版本鎖，多實例以最後成功保存為準。不能把測試成功當作突然斷電、磁碟硬體或所有檔案系統耐久性的證明。

## 證據與未驗收項目

SettingsStoreTests使用隨機自有暫存目錄，覆蓋缺檔／重載、選擇撤回、v1不覆寫、future／損壞／秘密與授權欄位拒絕、UTF8／大小、完整預算界線、端點共用驗證、保存失敗保留原檔、檔案鎖定，以及Dashboard兩列截斷。WPF smoke建立新視窗並載入合成設定，驗證無session、無Query／Export同意、來源仍為Demo，切回Demo後按保存預算查詢；沒有啟動外部AI或Power BI。

所有desktop smoke明確注入各自artifact下的fixture store，不讀寫正常使用者的偏好檔案。重載測試是新視窗與新store實例，不是完整OS重啟測試。人工設定表單、實際端點、檔案ACL／連結競態、MSAL安全持久快取、歷史紀錄與公開發佈仍需各自驗收或實作。
