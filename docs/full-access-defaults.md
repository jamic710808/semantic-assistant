# 預設全權限與固定啟動路徑

日期：2026-10-14。使用者已明確要求：送出對話不再顯示逐回合外送視窗，四個資料類別預設全部允許。

## 本次查到的根因

本輪開始時，根目錄 `Start-Semantic-Assistant.cmd` 仍指向 `artifacts/conversation-copy-context-20261014/SemanticAssistant.Desktop.dll`。
讀到的舊程序 PID 28128 也正在執行這個較早的程式包，沒有執行上一輪包含直接送出與 Markdown 排版的版本。後續檢查時，該舊程序已退出。

啟動器已改成固定 `app/SemanticAssistant.Desktop.dll`，避免每次發版以不同 artifacts 目錄作為實際安裝位置。根目錄的兩個中文啟動器沿用同一入口；先前的 `artifacts/ai-luna-fix-20261013/Start-AI-Fix.cmd` 亦改為呼叫根目錄入口。既有封存原始碼包與其他舊程式包沒有刪除。

## 全權限預設

2026-10-15 整理補記：根目錄僅保留新版中文啟動器與共用英文入口。上述舊 artifacts 程式包及 AI 修正版入口已移除；歷史紀錄／截圖在刪除前另存專案外備份。實際程式持續使用固定 `app/` 目錄。

- `AiTurnConsent.ForSend` 直接允許 UserMessage、Metadata、QueryResults、ExternalToolData 四類資料外送，不顯示選項視窗。
- 支援數值查詢的來源，預設啟用 DAX 查詢。
- 預設允許本機 HTML 匯出與已匯入外部工具的 AI 使用。
- 預設允許模型修改；已安裝的舊設定第一次載入會套用本次使用者要求，保存 `FullAccessDefaultsApplied` 標記，且先保留原設定備份。AI 模型清單與憑證不重設。
- 啟動、來源／模式切換、新對話、外部工具匯入及回合結束後，執行選項恢復預設允許。
- 後續若使用者在權限管理明確關閉模型修改，保存的選擇仍有效，不會在重啟時重複覆蓋。

「全權限」指本程式的資料類別與可用能力。離線來源不能執行 DAX、未匯入的工具不能呼叫，來源／版本與物件所有權檢查仍執行；模型實際修改及外部工具操作的預覽流程延續既有功能。此次移除的是送出對話時的資料外送授權視窗。

## 交付

本輪重新建置到固定 `app` 目錄，0 警告、0 錯誤，26.81 秒。包含上一輪的原生 Markdown 排版、格式化複製及乾淨對話。

後續亦完成 publish，為原生 EXE 設定 `AppHostRelativeDotNet=../../.tools/dotnet10`，可直接使用工作區既有 .NET 執行環境。新增 `scripts/Publish-Desktop.ps1`，未來發版統一更新 `app`，不再改回舊 artifacts 目錄。更新執行中的程式前應先關閉視窗，避免檔案使用中。

已在使用者桌面建立 `Semantic 智能助手.lnk`，目標是固定目錄的 `app/SemanticAssistant.Desktop.exe`。

實際交付證據：

- 以原生 EXE 啟動後，程序 PID 4212 的 ExecutablePath 是固定 `app` 目錄。
- 設定檔已保存 `AllowModelModification=true`、`FullAccessDefaultsApplied=true`，並保留 `settings-before-full-access-<隨機識別碼>.json` 備份。
- 新版已連回 `SemanticAssistantSynthetic`，畫面顯示4表、5度量值、2關係。
- 隨後讀到該視窗進入「正在思考…」，輸入框為「介紹這個PowerBI資料模型的基本結構」；此時沒有逐回合外送視窗。

本輪沒有新增或執行測試套件，沒有自行輸入／送出測試提問、執行 DAX 數值查詢或修改 Power BI 資料模型。已完成的是交付路徑與設定核對、啟動／唯讀連線及畫面狀態觀察，不等於全部 AI 工具與模型寫入流程的驗收。
