# 雲端登入快取：使用方式、契約與驗收

更新：2026-10-13。安全權杖快取（Token Cache）已接入委派登入與WPF；預設仍為記憶體模式。此功能讓使用者明確選擇保存同一帳號的登入，沒有保存模型來源、查詢結果或操作同意。

## 使用方式

1. 開啟「更多來源與工具」→「登入 Power BI 雲端」，填入自己的application及tenant GUID。
2. 預設不勾「記住登入30天」，每次由系統瀏覽器選帳號。本次不讀取或修改先前已保存的副本。
3. 若要續用登入，明確勾選記住登入。首次仍須互動登入；重新啟動後填相同兩個ID並再次勾選，才讀取已保存快取。ID與勾選值不寫入一般設定。
4. 「改選其他帳號」只對本次登入生效；成功驗證新帳號後才取代保存副本。取消／驗證失敗不覆蓋原檔。正在使用同一組快取的視窗會持有獨占鎖，須先登出或返回示範再重新選取。
5. 「清除此應用程式／租用戶的已保存登入」只清除相應本機檔；若正被另一實例使用會拒絕。已連線時可按「登出雲端並清除本機登入」，先撤銷本次操作同意、返回示範，再清除記憶體與保存副本。

雲端OAuth登入與產品的操作同意是分開的。快取中可能包含MSAL取得的讀取／寫入scope權杖，但不恢復model.definition.write、DataQuery、ArtifactWrite或AI外送同意；模型修改仍需當次預覽與逐次同意。登出不撤銷Entra既有授權、不清除系統瀏覽器cookie，也不能撤回先前已送出的請求或權杖。

## 保存與信任界線

| 項目 | 實作 |
|---|---|
| 入口 | `MsalUserIdentity(..., rememberLogin: false)`；未啟用時不建立或讀取快取目錄 |
| 位置 | `%LOCALAPPDATA%\SemanticAssistant\cloud-login\<profile-hash>.bin`；名稱由固定namespace、client與tenant的SHA256產生 |
| 保護 | `.NET ProtectedData`、DPAPI CurrentUser；profile hash亦作附加entropy；整份MSAL資料、帳號pin、版本與期限一起加密 |
| 帳號 | 只有保存的唯一HomeAccountId可默默續用；帳號不存在／多帳號／不符直接拒絕，沒有猜測或選取第一個帳號 |
| 服務回覆 | 在回傳access token或落盤之前重新檢查帳號、tenant與token expiry；明確改選帳號時才允許新pin |
| 保存時機 | 在MSAL通知中取得序列化資料；成功驗證AuthenticationResult、移除其他帳號後才提交；通知本身不寫磁碟 |
| 期限 | 成功互動登入起最長30天；silent refresh保存原期限，不藉由讀取或刷新延長；到期副本不匯入MSAL |
| 大小 | MSAL序列化512000 bytes、加密檔1048576 bytes；超限、毀損、無法解密、未知欄位、重複JSON欄位與版本不符皆拒絕 |
| 更新 | 同profile的檔案獨占鎖跨實例互斥；先寫加密暫存、flush，再原子替換；驗證失敗不覆蓋既有副本 |
| 檔案路徑 | 拒絕UNC、過長root、靜態reparse point／junction及以目錄冒充檔案；清除僅針對固定profile檔，不遞迴刪目錄 |
| 生命週期 | Dispose釋放記憶體序列化暫存與鎖，保留明確選擇保存的檔案；登出或明確清除才刪本機副本；0 bytes鎖檔可留存 |

DPAPI保護的是Windows使用者範圍，不是相同Windows帳號下其他程序的沙箱。使用者profile／OS遭入侵、同使用者惡意程序、硬連結與檔案系統檢查之間的惡意競態，不屬已證明隔離。JSON與MSAL內部仍有受GC管理的短暫明文物件；不能宣稱全部記憶體副本可安全抹除。加密暫存byte[]會清零，沒有明文快取檔、token log或工具回覆。

依據：[MSAL通知與序列化](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization)、[Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)。這是Windows專用、單profile獨占的有界實作，沒有宣稱具備官方跨平台Extensions.Msal的全部功能。

## 已驗證的範圍

- 新增32項案例：實際DPAPI往返與明文缺席、跨client／tenant複製拒絕、毀損／大小／期限、獨占鎖、原子替換、junction拒絕、帳號pin與服務回覆檢查、MSAL實際通知／本機反序列化、missing-account恢復拒絕、建構失敗釋鎖、取消與期限不續期。
- 完整455項測試通過，0失敗／0略過：`artifacts/test-results/cloud-cache-full-final.trx`。合成或空MSAL cache不是成功取得真實OAuth權杖的證據。
- 12條WPF流程全部通過：`artifacts/verification-cloud-cache-final.log`。新增流程建立真實登入表單，檢查兩個checkbox預設關閉，合成內容DPAPI往返，按表單按鈕清除，再檢查沒有恢復查詢／匯出同意。
- 表單影像：`artifacts/cloudlogin-form-preview.png`，GUID由當次測試產生。首次測試發現programmatic Window缺NameScope，修正後重跑；原始失敗與不含raw訊息的type／stack診斷保留。
- 新增Microsoft `System.Security.Cryptography.ProtectedData 10.0.0`，NuGet cache／lock／archive與notices核對通過。WPF由Windows Desktop framework提供此元件；一般net10.0雲端adapter／測試另引用套件。依賴報告區分10個桌面NuGet、1個framework提供及12個開發／測試套件，共23個。

## 待服務驗收與接續

尚未使用使用者的帳號、token或私人模型。Entra真正互動登入／silent refresh／Conditional Access／帳號切換、跨Windows使用者／漫遊環境、Power BI catalog／XMLA／OAuth scope與真實取消流程，均需要對應環境證據。此輪也沒有重跑前輪Dashboard真機；其34項成功紀錄保留為前輪證據。

下一步先整理可公開的來源庫與可追蹤驗收入口，再由指定的測試tenant與app進行服務驗收。公開remote、真實GitHub CI／release及第三方二進位分發條件仍未完成。
