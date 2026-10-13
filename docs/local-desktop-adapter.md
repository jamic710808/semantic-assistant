# 本機 Power BI Desktop 適配器

## 操作

「選取 Desktop」→ instance 選取 → catalog 選取 → metadata 檢視。這個操作讀取結構，不自動讀事實資料列。數值查詢需另外勾選 DataQuery，使用 AI 時還需 QueryResults 外送同意。

列舉從 Power BI Desktop 與 Store App 的 AnalysisServicesWorkspaces 目錄讀取小型 `msmdsrv.port.txt`，比對 Windows TCP listening port 的 msmdsrv PID／啟動時間。只接受唯一符合的 port owner；每次連線前後重新檢查。拒絕連結目錄／檔案、過長與非數字 port。變更時停止並要求重新列舉，不自動連接新目標。

TOM 負責中繼資料；ADOMD.NET 負責 DAX query。兩者用官方 Microsoft 套件，固定 localhost 目的地，用 connection-string builder 放入選取的 catalog，沒有讓 AI 提供連線字串。[Microsoft client libraries](https://learn.microsoft.com/en-us/analysis-services/client-libraries?view=azure-analysis-services-current)

## 查詢

- 只提供 ExecuteReader statement 路徑，没有 XMLA／writer port。
- 限 EVALUATE／DEFINE DAX envelope；此檢查不是完整 DAX parser，完整語法由服務端驗證。
- 最多 100 列、64 個欄位、64000 bytes，command timeout／CancellationToken／command.Cancel。
- 查詢前後檢查來源結構 revision，變更丟棄結果；超過列數或 bytes 的結果標記 Truncated，不冒充完整結果。
- revision 是表／欄位／度量值定義／單欄關係的內容 hash。它不是完整 TOM 定義版本或資料刷新時間，不提供跨查詢交易／一致快照保證。若未來新增 writer，需另設完整定義版本契約。
- 原生 Connect／ExecuteReader 可能短暫阻塞；UI 的 deadline 會丟棄取消後結果，server 取消是 best effort，不保證服務端立即停止。

## 驗證邊界

已驗證 UTF-8／UTF-16 port fixture、PID／啟動時間／埠變更、非法 port、DAX envelope 與來源種類切換。沒有執行原生 discovery 來探查使用者的現有 Desktop，也沒有對現有模型發送連線／查詢。真實 Desktop／Store 路徑、catalog 相容性、關閉／重開行為、TOM／ADOMD 驅動與通用 DAX 仍需在使用者選取的測試模型驗收。
