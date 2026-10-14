# 真實 AI 回合失敗排查

## GPT-6 Luna 與 Chat Completions

2026-10-13 查閱 [OpenAI GPT-6 Luna 官方模型文件](https://developers.openai.com/api/docs/models/gpt-6-luna)：
`gpt-6-luna` 支援 Chat Completions，但函式呼叫必須使用 `reasoning_effort: "none"`。
本專案會對 `https://api.openai.com/v1/chat/completions` 搭配這個確切模型自動加入該參數。
此路線不使用模型推理；若需要推理與工具呼叫，後續應實作 Responses API 配接器。
其他端點／模型不會自動套用這項設定。

舊版只顯示「回合執行失敗」，不能據此判斷 API key 或模型是否無效。
這次查閱原始碼確認缺少上述參數；尚未取得使用者當次 HTTP 回應，不能將它寫成已證實的當次根因。

## 如何重試

1. 關閉舊 Semantic Assistant，啟動修正後的桌面版。
2. 核對端點與模型；API key 僅在程式內設定。
3. 先用合成示範來源傳送「介紹模型結構」。按送出即使用目前供應商，已移除逐回合外送視窗。
4. 若仍失敗，提供狀態區訊息或側面板「執行紀錄」中的錯誤代碼即可；不要提供 API key。

## 新版錯誤分類

| 代碼 | 判讀 |
| --- | --- |
| `provider_request_rejected` | HTTP 400／422，核對模型與參數相容性 |
| `provider_authentication_failed` | HTTP 401，核對 API key |
| `provider_access_denied` | HTTP 403，核對專案、模型存取與政策 |
| `provider_not_found` | HTTP 404，核對端點、模型名稱與帳號權限 |
| `provider_rate_limited` | HTTP 429，核對速率與 API 配額／餘額；僅狀態碼無法區分兩者 |
| `provider_service_unavailable` | HTTP 5xx，供應商服務錯誤 |
| `provider_network_error` | 建立 HTTP 連線失敗 |
| `provider_protocol_error` | 非 SSE、串流格式不相容、截斷或超過大小限制 |
| `model_read_error` | 呼叫 AI 前讀取資料模型失敗 |
| `model_context_error` | 建立模型摘要失敗 |
| `tool_execution_error` | 本機工具執行階段失敗 |
| `provider_error` | 其他 HTTP 狀態或未分類回合例外，仍需進一步排查 |

這些訊息只使用本機固定文字與 HTTP 狀態分類，不顯示供應商回應內容、標頭、API key 或原始例外。
編譯成功不代表真實 API 驗收完成；真實服務、帳號存取與桌面互動需要另外驗收。
