# 啟動偵測 Power BI 報告與顯示名稱

## 啟動行為

一般桌面啟動在主視窗第一次呈現後，偵測本機工作區的服務埠及 msmdsrv 引擎。
有候選報告時顯示「選擇 Power BI 報告」視窗，列出名稱與連接埠，可取消或重新偵測。
沒有候選報告時保留合成示範主畫面；之後點「連接本地報告」仍可開啟選擇視窗。
取消不建立模型連線。連接按鈕才列舉所選 instance 的 catalog 並讀取結構。
單一 catalog 直接使用；多個 catalog 仍需再選一次，避免錯選內部模型。
啟動偵測不還原或新增 AI、數值查詢、外部工具或寫入授權。
合成預覽與既有 smoke 路線不會自動偵測本機報告。

## 名稱與識別碼

| 資訊 | 用途 |
| --- | --- |
| 報告顯示名稱 | 從 msmdsrv 的程序祖先找到 PBIDesktop，取得視窗標題；保留中英文 Unicode 文字，移除 Power BI Desktop 後綴 |
| Catalog ID／Name | Analysis Services 內部名稱，可能是 GUID；保留於模型詳情 |
| 模型 identity | PID、啟動時間、port、catalog ID；仍用於定位、版本與授權檢查 |
| Revision | 原有模型定義指紋；不因 UI 顯示名稱改變 |

報告名稱僅用於主畫面模型根節點、來源狀態、概覽與結果來源。
本次不更名 PBIX 檔案，不修改 Analysis Services 資料庫，也不重寫 GUID。
無法確認程序對應或視窗標題時顯示「未辨識報告名稱」及連接埠。
視窗標題不是完整檔案路徑，也不能保證與磁碟上已儲存的檔名始終一致。
標題在選取連線時刷新；後續另存新檔的名稱更新可重新選取報告。

## 實作

- `DesktopReportDiscovery.cs`：以 Windows Toolhelp32 程序表取得父程序，最多追溯八層；檢查程序啟動時間，避免 PID 重用造成錯誤名稱。
- `DesktopReportPicker.cs`：報告選擇視窗、名稱、port、取消、連接與重新偵測。
- `MainWindow.DesktopReports.cs`：啟動與手動入口共用流程；顯示名稱與內部 identity 分開。
- `DesktopModelAdapter.ReportDisplayName`：顯示用名稱，不影響連線字串、讀取／寫入 identity。

## 預覽與驗收

`--render-report-picker <PNG 路徑>` 使用明確標示的合成候選報告，輸出選擇視窗。
此輸出不顯示原生視窗，不偵測實機報告，也不讀取模型或憑證。
編譯及靜態預覽不證明實機程序對應、啟動彈窗或連接流程已驗收。
請以根目錄啟動入口重新開啟程式，確認正在執行的報告名稱，選取後確認左側模型樹。
