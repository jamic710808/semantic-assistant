# 計算欄位、關係與混合批次

日期：2026-10-13。自有度量值、計算欄位及單欄關係共用 writer／preview／approval／回讀／rollback；驗證只使用自製記憶體模型與未連線的 TOM 物件。

## 操作

桌面「更多來源與工具」→「模型修改預覽」：

1. 選「混合模型物件 JSON」。合成模型另提供「載入合成混合範例」，可直接走完整預覽與逐次同意流程。
2. `Measures`、`Columns`、`Relationships` 都必須存在；不用的填空陣列。合計最多16個物件，拒絕重複／未知欄位及數字 enum。
3. 核對模型、完整版本、欄位型別、DAX、關係兩端、基數、方向與啟用狀態，再勾選當次同意。
4. 成功後重新讀取模型；失敗會嘗試還原本次自有物件，部分失敗明確顯示操作 ID。沒有自動重試。

自製合成範例：

```json
{
  "Measures": [{ "Table": "銷售", "Name": "SA_混合批次", "Expression": "1" }],
  "Columns": [{
    "Table": "銷售", "Name": "SA_月份鍵",
    "Expression": "FORMAT('銷售'[日期], \"yyyy-MM\")", "DataType": "String"
  }],
  "Relationships": [{
    "Name": "SA_月份關係",
    "From": { "Table": "銷售", "Column": "SA_月份鍵" },
    "To": { "Table": "日期", "Column": "月份" },
    "IsActive": false,
    "FromCardinality": "Many", "ToCardinality": "One",
    "CrossFiltering": "OneDirection"
  }]
}
```

示範只驗證結構與授權，不計算新欄位的資料或驗證基數唯一性。真實資料模型請另選可修改的獨立測試模型。

## 契約與依據

- 欄位型別：String／Int64／Double／Decimal／Boolean／DateTime。只新增／更新自有計算欄位，不轉換既有 DataColumn、CalculatedTableColumn 或原廠物件。
- 更新自有欄位型別可預覽；若造成未修改的既有關係兩端型別不符，預覽拒絕。型別相容不代表 DAX 或資料必然有效。
- 關係基數：One／Many；篩選方向：OneDirection／BothDirections／Automatic。兩端需不同表、欄位存在且型別相同；可引用同批次新增欄位，避免先寫欄位再補關係的中途狀態。
- 同名非自有物件、欄位／度量值名稱碰撞、重複關係端點及無效版本拒絕。關係端點不依大小寫區別。
- 真實計算欄位寫入／還原會要求 `RefreshType.Calculate`，再 `SaveChanges`。重算成本及 DAX／基數／活躍路徑是否合法由引擎決定，沒有讀取私有資料列來預先猜測。
- 關係操作只設定兩端、基數、CrossFiltering 與 IsActive；不改 RLS 的 SecurityFilteringBehavior、其他安全設定或引用完整性政策。

類型／行為依據：[Microsoft CalculatedColumn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.analysisservices.tabular.calculatedcolumn?view=analysisservices-dotnet)、[SingleColumnRelationship](https://learn.microsoft.com/en-us/dotnet/api/microsoft.analysisservices.tabular.singlecolumnrelationship?view=analysisservices-dotnet)、[TOM JsonSerializer](https://learn.microsoft.com/en-us/dotnet/api/microsoft.analysisservices.tabular.jsonserializer?view=analysisservices-dotnet)。

## 所有權、完整性與還原

三種物件寫入時都標記 Owner／OperationId。原生 TOM writer 另將完整物件 JSON（排除完整性 annotation 本身）的 SHA-256 寫到 `SemanticAssistant.ObjectIntegrity`，不保存物件 JSON 或 DAX 原文到稽核 log。雜湊排序 object keys 與 annotation／extendedProperty 名稱，避免服務端重排無語意順序的內容造成誤判；其他陣列保留順序。

這可發現有人保留相同 Owner／OperationId、但修改額外 annotation、格式、RLS 方向等屬性的情況；預覽、回讀與還原會拒絕完整性不符。只有舊 Owner、沒有完整性 stamp 的物件不自動更新，需人工核對後移轉。

還原先檢查外部相依性，再還原關係及欄位。若其他人的關係、階層、排序欄、計算式、RLS 或 variation 引用了當次新增物件，拒絕刪除並回報 `partial_failure`。計算式引用檢查是保守的字串比對，可能誤擋；不是完整 DAX dependency parser。未知相依性、服務端產生的 metadata、實際 recalculation、競態及硬性取消仍須真實模型驗收。

完整性 annotation 不是服務端鎖或防偽授權。版本檢查與 stamp 都不能使外部 XMLA 成為原子交易；模型管理員仍可同時修改、移除或重新建立物件。詳見 [模型修改與 Dashboard](model-writing-dashboard.md) 的限制及修復方式。

## 證據

- `ModelObjectTests`：混合批次預覽、未授權拒絕、三種物件寫入／還原、碰撞、型別、物件更新與外部修改衝突。
- `TomObjectMutationTests`：在實際 Microsoft TOM 的自製 Database 物件上 stage → JSON projection → restore，沒有 Server.Connect 或 SaveChanges。驗證 annotation／SecurityFiltering／format 的完整性變動及外部階層相依性。
- WPF `--smoke-objects`：同一桌面預覽／逐次同意、混合物件更新、回讀與模型樹重新載入。

這些結果不證明真實 Power BI 資料、DAX 計算、RLS 或服務端交易已驗收。
