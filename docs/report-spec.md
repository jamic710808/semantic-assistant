# M4：受限報告規格（ReportSpec）

## 資料來源

ReportSpec v1 綁定一個成功查詢的 ResultId。VisualSpec 只選欄位索引，不接受數值陣列、HTML、JavaScript、外部資源 URL 或檔案路径。renderer 從 QueryResult 取值，避免 AI 自行填入數字。

| VisualKind | 規格 |
|---|---|
| Table | 所有回傳欄位；不接受運算／指定欄位 |
| Kpi | Count／Sum／First；Sum 與 First 需存在的數值欄位 |
| BarChart | label/value 欄位；不接受聚合；保留查詢回傳順序 |
| LineChart | 同上；連線代表回傳順序，不推定時間間隔等距 |

JSON parser 拒絕未知或重複 key、整數 enum、未知 kind、缺必要欄位、未知 ResultId、重複 visual ID、不存在的欄位、非數字、超過 +/- 10^24 的圖表數值。規格最多 32000 UTF-8 bytes、depth 16、8 個 visual；來源最多 100 列、64 欄、64000 bytes。

本機 renderer 統一驗證 JSON／程式模板，HTML escape 所有名稱與文字。圖表為 inline SVG、CSP default-src none；沒有外部 script、字型或圖表 CDN。未授權 ArtifactWrite 時不匯出；model definition write 是不同的權限。

預設數字欄位產生表格＋KPI＋長條圖，桌面可改成折線圖或只有表格。Count 是目前結果列數，Sum 是目前結果數值合計；截斷結果不等於完整母體。報告保留來源 synthetic 標記、revision、ResultId、truncation 與 DAX。

## 驗證

測試涵蓋 schema、來源 ID、數值／欄位、負數與零、HTML 注入、合計與截斷標記。瀏覽器已確認內附三列合成資料：120000、145000、132000，合計397000，SVG 正常顯示且沒有 console error/warning。報告目前採固定模板；未加入 AI 任意設計、互動 filter 或企業 KPI 語義推定。
