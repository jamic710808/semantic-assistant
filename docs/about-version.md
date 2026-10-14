# 版本編號與「關於」視窗

日期：2026-10-15。

## 版本

本輪開始時讀取固定 `app` 目錄的 Desktop DLL：FileVersion 為 `1.0.0.0`，ProductVersion 為 `1.0.0+795db6b498959b797052bff9629c3509aab999d9`。先前沒有為每次桌面改版另行指定版本，沿用 SDK 預設值；該 Git hash 也不能代表尚未提交的全部改動。

這次明確設定：

- 使用者顯示版本：**1.0.1（開發版）**。
- 完整產品版本：`1.0.1-dev.20261015`。
- FileVersion／AssemblyVersion：`1.0.1.0`。
- 建置時間：編譯時以 UTC 寫入 `AssemblyMetadata.BuildUtc`，畫面轉為本機時區顯示。
- 發行類型：`AssemblyMetadata.ReleaseChannel=Development`。

這是 Semantic Assistant 自己的版本，與原版 ABI 5.0.2 及 AI 供應商的模型版本分開。早期來源包的 r 編號亦不能直接當成桌面程式的語意版本。

## 介面

上方原有「關於」按鈕改開啟獨立 `AboutWindow`，參考使用者截圖的漸層底色、中央標誌、程式名稱、版本、簡介與確定按鈕排列。

顯示內容：

1. Semantic 智能助手。
2. 程式版本及開發版標記。
3. 建置日期、時間與時區。
4. 模型探索、AI 對話、DAX 查詢與報告功能簡介。
5. 自己的 GitHub 原始碼連結與原始碼 MIT 授權說明。
6. 版本文字的提示資訊含完整版本及目前程式位置，便於辨認是否誤開舊包。

`AppVersionInfo` 使用 `typeof(App).Assembly` 讀取**正在執行的桌面程式集**，不從新的磁碟檔案或 AI 模型推定目前版本。更新後未關閉的舊程式仍會使用自己載入的資訊。

## 交付

新版已 publish 到固定 `app` 目錄；根目錄及桌面入口沿用此位置。實際 DLL 的 FileVersion／ProductVersion 已讀回為 `1.0.1.0`／`1.0.1-dev.20261015`，生成的 AssemblyInfo 含建置時間 `2026-10-14T16:06:39.2458986Z`，即台灣時間 `2026-10-15 00:06:39 +08:00`。

以已安裝版本產生的「關於」圖片位於 `artifacts/about-v1.0.1-20261015/about-installed-preview.png`，已檢視文字與排版。沒有讀取憑證、呼叫 AI 或執行測試套件；舊設定、全權限預設與模型資料不因版本視窗變更而重設。

後續發版需更新 Desktop csproj 的 Version、AssemblyVersion 與 FileVersion，再使用 `scripts/Publish-Desktop.ps1` 發佈；新建置的時間會自動寫入。
