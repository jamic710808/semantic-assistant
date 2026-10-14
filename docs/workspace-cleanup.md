# 1.0.1 開發版工作目錄整理

整理日期：2026-10-15。桌面程式版本保持 `1.0.1-dev.20261015`。

## 最新版入口

- `啟動 Semantic 智能助手.cmd`：中文入口。
- `Start-Semantic-Assistant.cmd`：上述入口共用的啟動器。
- `app/`：唯一保留的桌面程式包，包含程式相依套件、語系資源及內附範例。
- 桌面捷徑指向 `app/SemanticAssistant.Desktop.exe`。

## 已清理

- 移除過時的 `啟動 AI 修正版.cmd` 與根目錄 `Start-Demo.cmd`。
- 移除 `artifacts/` 中的舊程式包、來源重建副本、舊封裝及驗收暫存。
- 移除 `src/`、`tests/` 下共 30 個 `bin/`、`obj/` 建置快取目錄。
- 上述產物／快取共 51,580 個檔案，約 11.23 GiB，不含兩個根目錄啟動檔。
- 歷史非二進位資料另存專案外私人備份；13,317 個檔案逐檔驗證 SHA-256，壓縮檔約 191 MiB。備份沒有放入來源庫。

## 保留與核對

保留 `.git/`、`.github/`、`src/`、`tests/`、`scripts/`、`docs/`、`examples/`、`third-party/` 及根目錄的方案、建置設定、README、授權與貢獻文件。它們是最新原始碼的開發與維護內容。

刪除前確認所有產物目錄均未追蹤至 Git、未含重新解析點，且沒有程序從這些目錄執行。完成後以刪除前建立的 SHA-256 清單核對 `app/` 的所有檔案，數量與內容均未變動。沒有重建程式或執行測試套件。

歷史分析／驗收文件保留原始時間與證據描述。其舊 `artifacts/` 路徑屬歷史紀錄，不是現行啟動入口；未重新執行的歷史驗收不視為本版通過。

## 下次建置

```powershell
.\scripts\Publish-Desktop.ps1
.\Start-Semantic-Assistant.cmd
```

SDK 會重新還原相依套件並產生 `bin/`、`obj/`；這些目錄及 `app/` 仍由 Git 忽略。開發用 `scripts/Start-Demo.ps1` 保留，可直接執行最新原始碼。
