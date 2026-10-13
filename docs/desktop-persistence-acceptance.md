# 合成 Desktop 保存與重開驗收

日期：2026-10-13。只使用本專案自行產生的合成 PBIP。

## 實際結果

Desktop `2.158.1177.0 (26.09)`：新模型通過34項引擎驗收，再透過Windows原生操作儲存、關閉。原Desktop／engine退出後，用新程序重開同一專案，20項唯讀保存驗收全部通過：程序重啟1項、定義與所有權1項、DAX6項、三種HTML×四種篩選12項。驗收沒有刷新、重寫或補蓋annotation。

成功證據：本機 `artifacts/live-fixtures/SemanticAssistantSynthetic-6cea43e43e9b41b48fdd25a58c10c6f2/persistence-verification-b62cb471ad814b60972a740ffc2076c7.json`，`SavedReopenedVerified=true`。產物不納入Git或來源ZIP。

首次失敗專案保留於 `SemanticAssistantSynthetic-84cd8804e28442ff93f580626c098f11`。確認Desktop存檔時替沒有譜系標籤（Lineage Tag）的新物件補上標籤，使原雜湊失效。修正為在度量值／計算欄封印完整性之前賦予GUID標籤；既有標籤保留，外部更改仍會產生衝突。沒有自動重新封印舊版失敗物件或放寬所有權檢查。

## 重現步驟

1. 執行 `.\scripts\Verify-LiveFixture.ps1 -KeepOpen`。只有成功才保留本次自有程序；不帶flag仍於結束清理。記下輸出的 `owned-desktop-session.json` 絕對路徑。
2. 在該合成專案的Power BI視窗按儲存（Ctrl+S），等待完成，再關閉該視窗。
3. 使用本次工具輸出的session路徑：

```powershell
.\scripts\Reopen-OwnedFixture.ps1 -OriginalSessionPath '<原 session 絕對路徑>'
.\scripts\Verify-PersistedFixture.ps1 `
  -OriginalSessionPath '<原 session 絕對路徑>' `
  -ReopenedSessionPath '<重開工具輸出的新 session 絕對路徑>'
```

重開工具限制此checkout的合成fixture目錄、拒絕reparse路徑／存活的原程序、建立新session而不覆蓋舊檔。不改用其他已開啟的Desktop。失敗只清理本次建立且PID／啟動時間仍相符的程序。

驗收執行檔比對四表inline M來源、預期新增物件、定義、owner／operation id／完整性，再執行固定唯讀DAX。沒有自訂查詢參數、不刷新或寫入，每次新建 `persistence-verification-<GUID>.json`；失敗保留證據並回傳非零exit code。完成後自行關閉合成專案。

## 邊界

這是特定合成PBIP及Desktop版本的證據，不能外推任意PBIX／私人模型或所有版本。CLI報告證明跨程序回讀及數值；本次原生儲存操作另由實際UI觀察確認。檔案marker不等於授權或簽章，session仍須是本次工具產生的自有專案。

待驗收：Assistant原生UI選取／操作、真實故障修復／競態、Power BI自訂HTML視覺與切片器、AI供應商、Entra／雲端服務。使用者指定AI供應商稍後設定，未讀取既有key或呼叫真實AI。
