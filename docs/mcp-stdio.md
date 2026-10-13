# MCP 外部程序適配器

日期：2026-10-13。程式碼與合成子程序驗證已完成；未啟動原廠或使用者設定的外部工具。

## 操作

1. 在桌面展開「更多來源與工具」→「MCP 外部工具」。
2. 選取完整可執行檔路徑，填參數 JSON 字串陣列及工作目錄。腳本透過明確指定的 interpreter 啟動；參數用 `ArgumentList`，沒有 shell 字串拼接。
3. 環境變數在隱藏欄位輸入 JSON；不保存設定或環境值。只繼承 SystemRoot／WINDIR／TEMP／TMP／PATH；其他變數須明確提供。
4. 點「啟動並列出工具」。這會執行外部程序及 initialize／tools/list，尚未呼叫任何工具。程序具有目前 Windows 使用者權限，沒有程序沙箱。
5. 選取工具，查看實際來源、工作目錄、PID、schema 及版本；輸入參數並預覽。
6. 勾選當次同意後執行。完成後清除同意；修改參數、換工具或重列清單後須重新預覽。

## 契約

- 支援 JSON-RPC 2.0 stdio、initialize、initialized、tools/list 分頁、tools/call、tools/list_changed；要求版本 `2025-11-25`，接受 `2025-06-18`／`2024-11-05`。不是全部 MCP 能力或所有新版協定的實作。
- client 不宣告 sampling、elicitation、roots；不自動處理 server 要求的其他動作。
- 每個工具要求 `ExternalTool`，來源提供的 readOnly annotation 不會升級本機權限。工具不自動加入AI；可明確匯入主視窗，另需當次啟用、外送與逐次操作同意。[共用policy契約](tool-registry-policy.md)。
- 當次 approval 綁定程序、tool／schema／catalog、參數雜湊、permission session/version 與期限；一次性使用。
- JSON Schema 使用 JsonSchema.Net 9.4.0；schema 建置／評估在自製 CLI 子程序執行，宿主只做有界格式檢查。禁止遠端 schema 抓取與重複 JSON 欄位。schema 16k、參數 16k bytes、訊息 64k bytes，並有深度、工具數與期限限制。
- 預覽先驗證參數；當次同意綁定已驗證的 schema／參數版本。宿主監測 worker 的 CPU、記憶體與牆鐘時間，Windows 另加 Job 限制；失敗、逾時、取消或異常回覆均禁止 tools/call。[隔離契約與驗證](mcp-schema-isolation.md)。
- stdout 僅接受協定訊息；stderr 排空後丟棄，不顯示或寫 log。遠端錯誤採固定訊息。
- 手動結果只顯示文字，不執行HTML、不跟隨資源連結；明確匯入AI後接受文字與structuredContent，另依ExternalToolData同意。
- 取消／失敗沒有自動重試，外部副作用可能已發生。手動視窗關閉等待請求結束並關閉自己啟動的程序；匯入後client交給主視窗，來源／模式切換、清除匯入或主視窗關閉才清理；必要時終止其子程序樹，不能還原已發生的外部副作用。

## 證據與限制

`tests/SemanticAssistant.FixtureHost` 是自製、實際啟動的 .NET 子程序；測試涵蓋 initialize、schema、逐次同意、版本變動、取消與一般化錯誤。WPF smoke 走同一桌面呼叫路徑。

Schema worker 的資源隔離已以自製實際子程序驗證，包含 CPU 忙迴圈、惡意正規表示式、取消與異常回覆；它不是外部 MCP server 的程序沙箱。一般 MCP 服務相容性尚未驗收，Streamable HTTP transport 尚未實作。

規格依據：[官方 stdio transport](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx)、[官方 tools](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/server/tools.mdx)、[JsonSchema.Net 文件](https://docs.json-everything.net/schema/basics/)。
