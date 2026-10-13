# 原始碼授權、相依套件與來源封裝

日期：2026-10-13。自行重寫的來源與文件採 MIT，見根目錄 `LICENSE`／`NOTICE`。這是本專案的授權決策；第三方套件各自保留其條件。尚無 remote、commit、公開 release 或已執行的 GitHub CI。

## 可直接執行

在 Windows、Git 與 `global.json` 指定的 .NET SDK 環境：

```powershell
.\scripts\Verify.ps1
.\scripts\Get-DependencyReport.ps1 -OutputPath artifacts\dependencies-current.json
.\scripts\Verify-Release.ps1 -BuildExtracted
.\scripts\New-SourceRelease.ps1 -Name semantic-assistant-source-preview
```

若依賴報告不同，先檢視套件版本、原始條款、來源與鎖定雜湊，再更新 `docs/dependencies.json`；release script 不會自動覆蓋這份審查基線。每個來源候選輸出 ZIP、逐檔 manifest、SHA256 檔案與依賴 JSON，到 `artifacts/releases/`。名稱已存在時拒絕覆蓋，請換新名稱。

來源 ZIP 可以解壓後以 `scripts/Verify.ps1` 建置；啟動仍使用 `Start-Demo.cmd`。Release 工具本身使用 Git 的追蹤／未忽略來源清單，須在 Git checkout 中執行。來源封裝沒有 .git、bin、obj、artifacts、NuGet 套件二進位、SDK、私人模型或設定檔。

`-BuildExtracted` 會在新建的 `artifacts/source-rebuild-<隨機識別碼>/` 解壓已驗證的來源，執行locked restore、Release build、全部核心測試、CLI與12條WPF合成流程；紀錄在該目錄的 `verification.log`。它使用既有SDK／NuGet cache，沒有內附SDK或離線還原套件，也不連接私人模型。只做封裝雜湊檢查時可省略此參數。

## 盤點方法與數字

目前鎖定 **23個NuGet套件：10個桌面NuGet相依、1個桌面framework提供、12個開發／測試相依**。新增ProtectedData 10.0.0宣告MIT並附第三方notices；WPF的assets `packagesToPrune` 顯示此元件由framework提供，因此不將它誤標為僅供測試。報告保留framework pruning range作核對依據。`docs/dependencies.json`記錄直接／間接lock file、版本、license expression／檔案／URL、repository commit（若提供）、copyright與內附條款雜湊。這是本機還原套件清單，不是完整SBOM認證或法律審核。

每個套件會比對 `.nupkg.metadata.contentHash` 與 lock 的 contentHash，另計算實際 `.nupkg` 的 SHA512 並比對 `.nupkg.sha512`。nuspec 及內附 license／notice 的位元組另比對實際 nupkg entry，避免將修改過的解壓檔當成原始條款。兩種值分開保存：NuGet content hash 排除簽章資訊，不能直接與整個 ZIP 的雜湊視為相同。腳本沒有獨立驗證數位簽章或重新計算 NuGet 的去簽章 content hash；locked restore 仍由 SDK 執行。[NuGet metadata 說明](https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File)、[signed package 說明](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference)。

本機 NuGet cache 的完整路徑不寫入報告。需要套件原始條款時，以報告中的 id／version 與 BundledNotices 路徑查閱還原套件；URL-only 套件需開啟對應上游網址。條款 hash 是指實際內附檔案，不把網站目前內容當成鎖定套件版本的條款快照。

## 已發現的第三方條件

| 範圍 | 鎖定版本／宣告 | 使用與發布注意事項 |
|---|---|---|
| json-everything 套件 | JsonSchema.Net 9.4.0、JsonPointer.Net 7.0.2、Json.More.Net 3.0.1，內附 OSMFEULA.txt | 上游來源 MIT；上游預編譯版本的條款要求符合營利活動及年總收入至少 US$10,000 條件的使用者支付維護費，另列豁免。條款允許自行編譯及按 MIT 條件再分發，不能簡稱下載的二進位只有 MIT |
| Analysis Services | AMO/TOM 與 ADOMD 19.118.3，license URL | Microsoft 獨立條款；公開分發 DLL 前需完成該版本分發條件與必要 notices 盤點，未宣稱授權清關 |
| Humanizer、MSAL 與 IdentityModel | package metadata 宣告 MIT | 保存相應來源與著作權；NativeInterop／Broker 是間接相依亦納入 |
| xUnit | metadata 宣告 Apache-2.0；abstractions 2.0.3 僅 license URL | 開發相依；上游條款仍記錄，未混入桌面相依分類 |
| Test SDK／TestPlatform／CodeCoverage | metadata 宣告 MIT，部分內附 ThirdPartyNotices | 開發相依；內附 notices 雜湊列入清單 |

json-everything 說明依據鎖定套件內的 OSMFEULA.txt；對照套件 nuspec 的 repository commit 之[上游條款](https://raw.githubusercontent.com/json-everything/json-everything/399f198431f65cf6896fe6038f833ef6d0b27a39/OSMFEULA.txt)。這裡記錄條款文字與適用條件，不推定使用者營收或個別豁免。來源碼自編譯是條款提供的選項；本輪沒有偷偷替換套件或倒退 Schema 實作版本。

Microsoft 原始 package URLs：[AMO/TOM](https://go.microsoft.com/fwlink/?linkid=852989)、[ADOMD](https://go.microsoft.com/fwlink/?linkid=852895)。本機取回 URL 會轉至 Microsoft 的 EULA ZIP；來源封裝並未收錄其 DLL，二進位分發審查保持未完成。

## 重現性與驗證邊界

來源 ZIP 使用固定內部根目錄、Ordinal 排序、2020-01-01 UTC 的 ZIP entry 時間與原始檔案位元組。`Verify-Release.ps1` 對同一來源／同一執行環境產生兩份 ZIP，逐檔比對 manifest、確認整包 SHA256 相等，並測試 19 個允許／拒絕路徑與拒絕覆蓋。這證明同環境來源封裝的重現性；尚未證明不同 .NET／PowerShell／壓縮實作之 ZIP 或發布的 DLL 位元組相同。

路徑 allowlist 拒絕生成產物、.env、常見憑證及 Power BI 二進位模型；檔案／父目錄拒絕 reparse point。這不是內容層祕密掃描或第三方資安認證，manifest 明確保留 `SecretContentAudit=false`。不因檔案副檔名合格便宣稱檔案內容沒有祕密。

`source-preview.yml` 只在明確啟動 workflow 後驗證並上傳來源 artifact，不自動 push、tag、修改 repository 或建立公開 GitHub Release。CI 定義不是已執行的 CI 證據。

## 下一步

1. 在獨立測試模型驗收真實 Desktop／DAX／writer，保持現有私人模型不參與測試。
2. 決定 json-everything 套件維護費／來源自編譯方案，並補 Microsoft 二進位分發條款及 notices。
3. 確定 GitHub 目的地、完成來源審查、commit／push 並取得真實 CI 成功證據，再建立公開來源 release。
