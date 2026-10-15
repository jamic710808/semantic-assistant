# Semantic Assistant 應用程式圖示

日期：2026-10-15。依使用者提供的參考圖，以內建 imagegen 製作透明背景的鋼藍色圓角圖示，包含銀色齒輪、盾牌、金屬 S、左下 `.exe` 及右下 `AI`。

## 檔案與整合

- `src/SemanticAssistant.Desktop/Assets/SemanticAssistant.png`：透明 PNG 設計原圖，工具實際輸出 1254 × 1254 px，保留原始尺寸與透明度。
- `src/SemanticAssistant.Desktop/Assets/SemanticAssistant.ico`：Windows 圖示，包含 16、20、24、32、40、48、64、96、128、256 px，32-bit RGBA。
- `scripts/Convert-AppIcon.ps1`：以 Windows System.Drawing 將 PNG 轉為多尺寸 ICO，保留透明度；不需要 Python 或額外套件。
- Desktop 專案的 `ApplicationIcon` 將 ICO 嵌入 EXE；WPF 資源及主視窗使用同一圖示。
- 發佈時也將 `SemanticAssistant.ico` 複製到 `app/`，供桌面捷徑指定。

重新產生與編譯：

```powershell
.\scripts\Convert-AppIcon.ps1
.\scripts\Publish-Desktop.ps1
```

版本仍為 `1.0.1-dev.20261015`；此變更只增加圖示及對應建置資源。圖示外觀參考由使用者提供，未從 ABI 安裝檔擷取圖示。

本機已發佈至固定 `app/`，並從 EXE 擷取到新的 32 × 32 圖示。ICO 目錄確認包含 10 個影像，PNG 左上角 alpha 為 0。桌面捷徑已指定 `app/SemanticAssistant.ico,0` 並通知 Windows 更新圖示；沒有執行測試套件。

## 啟動修正

上述最初的圖示檢查沒有實際啟動主視窗，因此漏掉 WPF 資源解析衝突。使用者回報後，直接執行已發佈 EXE 重現立即退出（退出碼 `-532462766`）。Windows .NET Runtime 紀錄顯示 `XamlParseException`，內層為找不到 `app/Assets/SemanticAssistant.ico`。

原因是同一 ICO 同時宣告為 `Resource` 與 `Content`。WPF 產生 `AssemblyAssociatedContentFileAttribute("assets/semanticassistant.ico")`，載入視窗時優先尋找外部 Content 檔案；實際檔案卻已複製到 `app/` 根目錄。修正為 `Resource` 加上可複製的 `None` 項目，將外部捷徑用 ICO 與內嵌 WPF 資源的解析方式分開，不再產生該 Content 宣告。

修正後已重新發佈至 `app/`，確認生成的 Content 宣告不含 ICO。直接啟動相同 EXE 並觀察 12 秒後，程序仍存活、主視窗標題為「Semantic 智能助手」、視窗 Handle 非零且 `Responding=true`。視窗保留開啟供使用者操作。這次是實際啟動確認，沒有執行測試套件或發送 AI 請求。

## 產生方式與完整提示詞

使用內建 imagegen，參考圖作為圖像輸入，`transparent_background=true`。輸出原圖保存在專案 Assets 目錄。以下為實際提示詞：

```text
Use case: logo-brand. Create one polished Windows desktop application icon for Semantic Assistant using the attached image as the visual reference. Deliver a single square 1024x1024 RGBA image with actual transparent background outside the icon. Faithfully match the reference's rounded-square dark steel blue beveled tile, silver mechanical gear around a central blue metallic shield, and prominent raised silver-to-champagne-gold uppercase S. Preserve the small .exe lettering at bottom left and AI at bottom right, as in the reference. Front-facing, symmetric, premium realistic 3D metal, crisp contours, restrained highlights, dark navy inset, subtle violet steel on shield, highly readable S. Let the tile occupy about 90 percent of canvas with transparent breathing room all around. This is the actual icon asset, not a mockup. Remove the outer dark rectangular backdrop and the caption Style 5 below the tile. No extra words, no watermark, no extra icons. Keep the central S bold enough to work when downscaled to 16 and 32 pixels.
```
