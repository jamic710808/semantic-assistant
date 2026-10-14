# 直接送出與格式化對話

日期：2026-10-14。依使用者的 ABI 參考截圖調整送出流程與回答排版。

## 1. 直接送出

選好目前 AI 模型後，按「送出」即以該供應商處理當前提問、可用前文與模型結構，不再跳出「當次回合的資料外送授權」視窗。

`AiTurnConsent.ForSend` 在每次送出建立最長10分鐘的當次授權：

- 預設提供 UserMessage 與 Metadata。
- 數值結果在啟用數值查詢，或前文已有仍有效的數值依據時提供；沒有因此自動開啟新的 DAX 查詢權限。
- 外部工具資料在使用者啟用已匯入工具，或前文已有仍有效的工具依據時提供。
- ProviderAuthorization、來源版本、PermissionSession 與取消機制繼續限制當次回合；使用者不需要再操作中繼資料外送選項。

模型修改總開關、實際差異預覽與寫入同意，以及外部工具的實際操作預覽沿用原流程。這次移除的是使用者指出的逐回合資料外送視窗。

AI 配置頁與側面板文案已同步，避免仍顯示「每次需同意外送」。保存或切換模型本身不會呼叫 API。

## 2. 回答排版

原本以唯讀 TextBox 顯示 Markdown 原文；標題、清單與粗體語法因此露出。現在使用 `MarkdownConversationView` 與原生 WPF FlowDocument／RichTextBox：

| 內容 | 呈現 |
|---|---|
| 一級／二級／三級標題 | 28／24／20 字級，粗體及段落間距 |
| 本文 | 14 字級、25 行高，深色文字，段落留白 |
| 粗體／斜體 | 原生字型樣式 |
| 無序／有序清單 | 原生圓點／編號與縮排；可包含巢狀清單 |
| Markdown 表格 | 表頭、分隔線、儲存格留白 |
| 行內程式碼 | 等寬字體、淡底色；標題中的字級沿用標題 |
| 程式碼區塊 | 等寬字體、淡底框線及內距 |
| 引述、分隔線 | 獨立排版 |

使用 Markdig 1.3.2 解析 Markdown 語法樹，再建立原生文件元素；沒有把 AI 文字載入 HTML／XAML。僅啟用所需 Markdown 與表格能力，HTML 解析停用，遠端圖片不會自動下載。一般 HTTP／HTTPS Markdown 連結可由使用者點擊開啟；自動連結保留可讀網址。

回答完成後捲到新回答的開頭，保留標題可見。主對話沿用上一版規則，只包含提問與最終回答；工具紀錄仍在預設收合的側面板。

系統指令亦要求開頭短摘要、清楚主題層級、清單、適度強調與段落間空行。一般結構介紹不列模型 GUID、來源雜湊、工具名稱與讀取流水帳；真正影響結論的資料限制仍保留。AI 摘要使用報告顯示名稱，原生模型識別與版本不變。

## 3. 複製與報告

- 文字可選取與使用 Ctrl+C。
- 「複製最新回答／全部對話」提供 Unicode 純文字及 RTF，貼入支援 RTF 的 Word 可保留標題、粗體、清單等格式。
- 同時提供原始 Markdown 剪貼簿格式；RTF／剪貼簿失敗時改嘗試複製原始文字。
- ReportDocument、AI 前文與報告匯出仍使用原始 Markdown；顯示歷史是單獨的文件元素，不用從畫面反解析回答。
- 模型修改後重新讀取來源時，使用訊息快照恢復已顯示的格式化對話，不把技術收據串進正文。

目前的富文字格式是 RTF＋文字，不等同 ABI 的 HTML＋文字剪貼簿。圖片、數學公式、互動圖表及全部 Markdown 擴充並未在這次交付中實作。

## 4. 交付與證據

程式包：`artifacts/readable-chat-20261014`；根目錄啟動器已更新。新增依賴已鎖定於 packages.lock.json，Markdig 的 BSD 2-Clause 授權聲明隨程式包一併提供。

後續已統一實際安裝位置為固定 `app` 目錄，並設為對話四類資料與支援能力預設允許；根目錄及桌面啟動入口均已更新。詳 [預設全權限與固定啟動路徑](full-access-defaults.md)。舊 artifacts 為歷次交付保留，不應再作為目前版本的啟動位置。

最後一次 Release 建置：0 警告、0 錯誤，8.91 秒。

使用正式排版器產生的範例：

- `artifacts/readable-chat-20261014/answer-layout-preview.png`
- `artifacts/readable-chat-20261014/answer-layout-preview.rtf`

這些是使用本次提供的模型結構文字製作的**排版示例**，不是新 AI 回答。生成過程沒有開啟原生視窗、讀取憑證、呼叫 AI 或連接 Power BI。示例已實際產出並檢視圖片，標題、清單、粗體、表格與程式碼區塊均以格式化內容呈現。

本輪沒有新增或執行測試套件，亦未重跑真實供應商回合或操作 OS 剪貼簿。示例輸出與建置不能代替真實 AI、Word 貼上、DPI／縮放、長答案與多回合捲動的互動驗收。開啟中的舊版本需重新啟動，才會使用新流程。

## 來源

- [Markdig 官方原始碼](https://github.com/xoofx/markdig)與[套件版本](https://www.nuget.org/packages/Markdig/1.3.2)。
- [WPF FlowDocument 官方原始碼](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/FlowDocument.cs)。
