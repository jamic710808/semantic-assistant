# HTML 報告匯出與瀏覽器開啟

日期：2026-10-15。使用者在 HTML 模式產生分析正文並點擊「匯出／開啟 HTML 報告」，Chrome 顯示 `ERR_FILE_NOT_FOUND`。

## 已確認

- 原始 HTML 實際存在於 `%LOCALAPPDATA%/SemanticAssistant/reports/`，大小 17,711 bytes，具有完整 HTML 結尾，可以讀取。
- 原始路徑長度 148 字元；Windows 預設 HTML 關聯為 Chrome。Shell 接受開啟指令，不等於瀏覽器已成功讀取檔案。
- 將同份檔案複製至使用者「文件/Semantic Assistant Reports」，核對 SHA-256 一致，再以標準 `file:///` URI 開啟後，使用者確認可以看到內容。
- 原目錄讀取失敗的完整環境原因尚未確定；不能據此認定所有 Chrome 都無法讀取 AppData。

## 修正方式

`LocalHtmlReportLauncher` 保留應用程式管理的原始匯出檔，在使用者「文件/Semantic Assistant Reports」建立瀏覽器用副本，完整關閉輸出串流後，以 `Uri.AbsoluteUri` 交給預設瀏覽器。副本使用簡短的內容雜湊檔名；相同內容重用既有副本，使用者修改過的副本不覆蓋。

「已匯出」與「瀏覽器開啟未完成」分別顯示。自動開啟失敗時仍回傳匯出結果、保留檔案並顯示原始位置，不再一律報成匯出失敗。查詢結果過期另有明確訊息。

沒有修改瀏覽器的安全設定、檔案權限、系統預設關聯，也沒有啟動 HTTP 伺服器或上傳報告。修改處理的是檔案交付流程，HTML 正文不變。
