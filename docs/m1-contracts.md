# M1 執行契約

## 版本與相容範圍

`ConversationEvent.Version=1`；每個回合有獨立 Guid RequestId，Sequence 從 1 遞增。WPF 直接呼叫 Core，只有輸出事件，不接受外部 JSON bridge。未來網路／WebView 輸入若新增，必須明確拒絕未知版本，不能把現有 DTO 當成已完成輸入認證。

`PermissionSnapshot` 固定 session ID、版本、ModelIdentity（ID、名稱、revision、合成標記）、mode 及不可變 capability 集合。PermissionSession.Update 使先前快照失效。查詢結果及報告須對應相同 target/revision。M1 Analyze／Report 共用唯讀工具；尚未增加不同 mode 的工具。

## 工具

| 名稱 | JSON 參數 | 上限 | Capability |
|---|---|---|---|
| get_table_details | `{"table":"銷售"}` | 字串 1–128 字元，不接受未知或重複 key | MetadataRead |
| execute_dax_query | `{"dax":"..."}` | 字串 1–8000 字元，不接受未知或重複 key | DataQuery |

兩者都必須是物件、指定參數必填且是非空白字串。未知工具直接拒絕，先驗權限才驗參數／呼叫 adapter。JSON Schema 與本機驗證器共同受測試。查詢限制在 adapter 與 dispatcher 兩層檢查；正式 adapter 仍須在查詢執行階段實作 server command timeout 及結果讀取大小限制。

完整 ToolCall 由 provider 交給 core；M1 沒有 external streamed JSON parser。ToolCall.Id 同回合不重複，max 128 字元；不同回合互不共用去重集合。工具順序執行，沒有並行、沒有自動重試。

## 預設限制

- 6 個 provider rounds：每次呼叫 StreamAsync 算一個。
- 8 次工具呼叫：包含被拒絕的工具結果。
- 總文字 20,000 字元；使用者訊息 1–4,000 字元。
- 總回合 15 秒；單次 query 3 秒。
- query 最多 100 列、64,000 UTF-8 bytes（序列化結果封套也計入）。
- context 最多 20 張表名稱、2,000 字元；不含連線、M expression、事實資料列。
- HTML report 最多 100 列、256,000 UTF-8 bytes；host 固定目錄、隨機檔名、CreateNew，不接收 provider path。

等待超時及 CancellationToken 不代表同步原生引擎已被終止。Adapter/provider 必須遵守取消；M2/M3 實際引擎連線前需另作非合作取消實驗。檔案 I/O 已開始後撤銷可能留下一份檔案，但不会回報匯出成功。

## 錯誤碼

| 層 | 碼 |
|---|---|
| 工具／policy | unknown_tool、permission_denied、stale_permission、invalid_arguments、table_not_found、target_changed |
| query | query_timeout、invalid_provenance、result_limit、adapter_error |
| 回合 | invalid_message、invalid_mode、external_provider_disabled、invalid_call_id、tool_limit、round_limit、text_limit、turn_timeout、cancelled、provider_error、ok |

工具拒絕以 ToolResult 回給 fake provider；整個回合仍可能正常結束，因此 Outcome=ok 不代表每個工具成功。UI 同時顯示 ToolCompleted.Code。內部 exception、連線資訊不放到 events。此規則未對「模型自己在文字裡輸出敏感資料」提供內容辨識；M1 沒有接真實來源或外部模型。

## M2 離線 TMDL

明確選取資料夾才讀檔；使用 Microsoft.AnalysisServices 19.118.3 的 `Microsoft.AnalysisServices.Tabular.TmdlSerializer.DeserializeDatabaseFromFolder`。官方文件說明此方法把 TMDL 文件反序列化為記憶體中的 Database。[官方 API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.analysisservices.tabular.tmdlserializer.deserializedatabasefromfolder?view=analysisservices-dotnet)

Reader 不建立 Server、不 Execute、不 SaveChanges。只回傳表、欄型別、度量值 DAX、single-column relationships；未回傳資料來源設定、partition M expression、憑證、角色、透視表等。上限 512 個 TMDL 檔、512 個目錄、每檔 1 MiB、總計 8 MiB；拒絕 reparse point。讀取前後內容 SHA-256 需一致，模型 ID 不暴露完整路徑。這是針對一般本機檔案的防護，不是隔離惡意程序的檔案系統沙箱。
