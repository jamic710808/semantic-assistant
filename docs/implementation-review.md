# 實作覆蓋與接續審查

日期：2026-10-13。這份文件記錄目前自行重寫的實作狀態，不把原廠不可觀察的方法本體推測成事實。

## 結論

核心、適配器與桌面介面已分離，可由自己的原始碼和鎖定套件建置。M1–M5 的所列程式碼有本機／合成驗證；M6 的自有模型物件、Dashboard、固定／動態 HTML 度量值與AI結構化提案已接入。**真實服務、動態DAX引擎與HTML視覺驗收仍未完成。**

| 範圍 | 可直接追蹤的呼叫鏈 | 實作／驗收邊界 |
|---|---|---|
| 離線結構 | MainWindow → ModelWorkspace → TmdlModelReader → TOM deserialize → MetadataInspector | 合成 TMDL 與桌面通過；使用者 PBIP 未驗收 |
| AI 分析 | MainWindow → PermissionSession／ProviderAuthorization → ConversationCoordinator → SSE provider → ToolDispatcher | 完整串流與回合授權；外部供應商未驗收 |
| 查詢／報告 | ToolDispatcher → readonly runner → QueryResult → ReportSpecs → renderer／exporter | 合成／loopback 通過；真實 DAX、資料穩定性另驗收 |
| 雲端 | 使用者登入 → workspace／dataset 選取 → XMLA metadata／REST query | fake HTTP／token 通過；未登入真實租用戶 |
| MCP | 程序設定 → tools/list → 明確選取／ToolRegistry → ToolPolicyEngine → 隔離Schema／參數預覽 → 一次性lease → tools/call | 手動及AI共用policy；真實自製程序／loopback HTTP與WPF通過；一般MCP服務及真實AI未驗收 |
| 情境摘要 | 外送／讀取授權 → snapshot／版本檢查 → ModelContextBuilder → 表名選擇／預算 → provider | 可替換策略；不輸入定義或查詢列；合成授權與預算測試 |
| 結果期限 | adapter → 唯讀5分鐘期限 → 核心／AI重查 → 桌面timer → 匯出／快照與子寫入檢查 | UTC與單調時鐘；合成時鐘驗證拒絕及UI清除；不宣稱記憶體安全抹除或已送內容回收 |
| 操作模式 | MainWindow → ModeController → 路由／能力交集 → AI工具或本機預覽 → 重新檢查 | 五工作流程；模式切換撤銷同意，拒絕晚到結果；不是五種AI工具模式或原生I/O交易 |
| 可選歷史 | 回合固定摘要／匯出檔名 → opt-in分類政策 → FileHistoryStore → 列表／移除／清除 | 預設關閉、期限／容量／mutation lock；不保存對話／查詢列／key或恢復操作同意；HTML另存 |
| 設定保存 | 明確記住profile／預算 → v2驗證 → bounded UTF8／flush／同目錄替換 → 新視窗重載 | 不恢復source、權限或外送；無效／future設定保留，v1不自動重寫；合成store／WPF通過 |
| 自有模型物件 | ModelWriteWindow → ModelWriteCoordinator → TOM writer → 完整性／回讀 → scoped restore | 三種物件、混合批次、外部修改／相依性及未連線 TOM 通過；真實 XMLA commit／重算／還原未驗收 |
| Dashboard | AI propose_dashboard／手動計畫 → 欄位／視覺驗證 → 修改差異 → 逐次寫入 → 另行查詢 → ReportSpec | 來源綁定提案、無自動查詢／寫入、自製固定計畫通過；真實服務未驗收 |
| HTML 度量值 | 固定結果／ReportSpec或動態Plan／已回讀指標 → 固定模板 → DAX → 獨立預覽與同意 | 四種動態模板、上限／跳脫／碰撞、保存／回讀通過；實際DAX／Power BI視覺呈現未驗收 |

## 本次審查修正

- 返回示範丟棄舊 mutable adapter，避免新 snapshot 使用舊 revision。
- 視窗關閉等待取消、原生寫入與還原；MCP 關閉等待自己啟動的程序清理。
- 原生 restore 不被 WaitAsync 提早分離；repair token 可撤銷，不能宣稱硬性終止 SaveChanges。
- 完整 TOM 定義 revision 與 readonly metadata revision 分離；測試證明 annotation 變化會進入完整版本。
- 雲端 preview 使用唯讀 token，apply 才可能向原帳號請求寫入 scope；restore 不自動要求互動登入。
- JSON 拒絕未知／重複欄位；供人審查的文字欄位直接顯示中文名稱與 DAX。
- durable intent 先於修改；audit 結束失敗不錯誤刪除已回讀成功的物件。
- AI提案只提供 MetadataRead，不暴露 writer；完整成功且授權仍有效的回合才接受來源綁定計畫。
- 動態HTML依目前指標產生DAX；KPI只聚合顯示的前100組，保留輸出限制與分類折線／空白行為說明。
- HTML的子寫入視窗保留產生時版本；背景變更後拒絕舊模板，不能只更新來源版本便繼續預覽。

## 接續順序

1. 動態HTML與AI提案已補入，[完整契約](dynamic-html-dashboard-proposals.md)記錄上限、來源及尚未驗收行為；以獨立測試模型驗證實際篩選與引擎計算是後續服務驗收項目。
2. [覆蓋審查](scope-audit.md)已保存缺口。情境建構器、結果期限與設定保存已接回核心／桌面；五模式控制與可選歷史已接入；共用工具政策與MCP明確匯入已接入；MIT來源授權與來源封裝已加入，接續公開release與獨立真機驗收準備。
3. MCP schema 隔離已加入，共用工具契約已加入，接續常見外部服務相容性及 release 封裝。
4. 由使用者另選可修改的獨立測試模型，進行 Desktop／雲端服務驗收；目前不連接既有私人模型。
5. MIT來源授權與22套件條款清單已加入；補第三方二進位分發條件、設定 remote 與公開release／CI；尚未 commit、push 或公開發佈。

自製 fixture 是驗證來源，不能替代生產 Power BI 的語意、效能、授權、競態及外部服務條件。WPF 是目前桌面宿主；WebView2／React 是未實作的替代呈現選項，不宣稱已有 bridge。
