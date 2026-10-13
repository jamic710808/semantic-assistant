# M3：真實 AI 供應商驗收工具

日期：2026-10-13。這個入口支援 Chat Completions 相容串流；驗收工具、注入測試、TCP loopback 與真實供應商驗收分開記錄。尚未設定供應商或尚未授權時，不宣稱真實服務通過。

## 先做預檢

Windows 安裝 `global.json` 指定的 .NET SDK 後執行；腳本會以 lock files 還原並建置驗收工具：

```powershell
.\scripts\Verify-AiProvider.ps1
```

預設使用 `examples/ai-acceptance/profile.example.json`。四項本機檢查涵蓋沒有同意、同意過期、目的供應商不符及授權撤銷；不得讀取 API key、模型結構或執行查詢，也不得發送 AI HTTP。其餘四項標成 `not-run`。每次建立新的 `artifacts/ai-acceptance-<GUID>/verification.json`；既有目錄或紀錄不能被覆蓋。預檢已接入 `Verify.ps1` 與 CI。

## 稍後設定供應商

1. 複製範例 profile 到本機另一個 JSON 檔案。填入實際完整 `/chat/completions` HTTPS 端點與模型名稱；本機服務可用 loopback HTTP。`Provider.Id` 保持 `openai-compatible`，與桌面設定一致。`TimeoutSeconds` 是每階段 5–120 秒上限。
2. 在 Semantic Assistant「設定 AI 供應商」填入完全相同的端點與模型，透過該畫面儲存 API key。認證 target 使用完整 provider 身分的雜湊；不同模型或端點不會沿用另一個 target 的 key。無 key 的本機服務仍須明確外送同意。
3. profile 不接受 API key、header、私人模型、查詢、prompt 或權限欄位。勿將真實供應商設定或憑證加入 Git。
4. 再明確執行：

```powershell
.\scripts\Verify-AiProvider.ps1 `
  -ProfilePath 'C:\你的目錄\ai-acceptance-profile.json' `
  -Live -AllowMessageEgress -AllowMetadataEgress -AllowQueryEgress
```

四個 flags 缺任一個，就在讀 key 或 HTTP 前拒絕。參數只授權本次驗收中的合成訊息、合成結構與合成查詢結果；不保存操作同意。不自動讀取桌面 preferences、既有 Power BI 報表或資料表。

## 真實端點的四項檢查

| 檢查 | 必須觀察的證據 |
|---|---|
| message-only | 成功收到串流文字；即使本機有結構／查詢能力，未授權的結構不得讀取、工具不得外送、數值不得查詢 |
| tool-roundtrip | AI 先讀合成表中繼資料，再使用內附固定 DAX；每個工具均成功，三列結果成功回傳下一輪 HTTP，模型在收到結果後回覆隨機標記 |
| cancel-on-text | 首段非空文字觸發取消；回合回傳 `cancelled`，不得發出後續文字或執行查詢 |
| revoke-on-text | 首段非空文字撤銷該回合權限；回傳 `stale_permission`，不得發出後續文字或執行查詢 |

工具最多三輪、六次工具呼叫、4000 字元；整份驗收最多12個AI HTTP請求。每階段有獨立期限；沒有自動重試。任一階段失敗或沒有觀察到必要行為，後續階段標成 `not-run`；可用 Ctrl+C 取消。

## 報告與判定

- 報告只保存 provider 身分（`profile.Provider` 正規化後 JSON）的 SHA-256、時間、固定 gate／狀態／錯誤碼與 HTTP／憑證讀取／模型讀取／查詢次數。不保存 key、端點、AI 回覆、原始錯誤 body、prompt、DAX 或結果列。
- `LocalPreflightPassed` 只證明本機拒絕檢查。
- `ConfiguredEndpointVerified` 需要明確 live flags、實际 HTTP transport 與全部八項檢查通過。注入 handler 測試即使全過，此值也維持 false；`Transport` 顯示 `injected-test-transport`。
- 實際 loopback 端點通過只證明所設定的本機端點協定與流程；不能推出它背後使用何種模型或遠端服務。
- `GeneralModelQualityVerified` 維持 false。固定合成任務不能证明任意報表分析品質、真實雲端權限、RLS、Power BI UI 或整體產品目標完成。
- 回合可能在產生 API 使用量後失敗；已外送的內容不能收回。CLI 成功退出不代表任何模型寫入或原生介面操作已驗收。

Windows 憑證元件移到 AI adapter，桌面與驗收工具共用原有 `SemanticAssistant/AI/` 命名規則；沒有遷移、列舉或讀取別的程式憑證。本文不宣稱使用者已設定或驗證真實供應商。
