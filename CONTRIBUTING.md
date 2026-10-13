# 開發與貢獻

1. Windows 安裝 `global.json` 指定的 .NET SDK；執行 `scripts/Verify.ps1`。非 Windows 只能開發 Core/CLI，整份 solution 的 WPF 建置需 Windows。
2. Contracts 不依賴 UI、TOM 或 AI 廠商；Application 透過 ports 存取 adapter。Desktop/CLI 負責 composition。
3. 原廠 binary、反編譯結果、prompt、圖示、模板與私人模型資料不可放入此來源庫。測試僅使用自行建立的合成資料。
4. 預設只讀；新增工具須有 schema、capability、本機校驗、受限結果與拒絕行為測試。不能只依賴「沒有列給模型的工具」作為權限保證。
5. 本機資料讀取與外送 AI 必須分別授權。模型寫入與MCP由獨立適配器／當次同意控制；Demo的記憶體writer只保存自製合成狀態，不連接真實模型。
6. lock files 必須一起更新。穩定錯誤碼、取消語意及事件契約見 `docs/m1-contracts.md`。
7. 自行重寫來源採 MIT，見 `LICENSE`／`NOTICE`。第三方條款與精確版本見 `docs/dependencies.json`；更新套件時須重新盤點，不能將第三方二進位改標 MIT。來源 release 使用 `scripts/Verify-Release.ps1`／`New-SourceRelease.ps1`；二進位分發審查另見 `docs/release.md`。不要宣稱與 ABI Assistant 功能或安全等價。
