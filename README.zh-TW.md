![MDNote](screenshots/banner.png)

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md) · [简体中文](README.zh-CN.md) · **繁體中文**

像記事本一樣輕巧快速的 Windows Markdown 與 TXT 編輯器。

## MDNote 的優勢

- **不必記語法符號的 Markdown**：在像 Word 一樣的畫面上直接寫作，檔案會儲存為標準的 Markdown 格式。
- **如記事本般輕巧，大型文件依然快速**：安裝檔約 1.3MB。數百萬字的文件也能立即開啟，打字不延遲。
- **一個程式同時處理 MD 與 TXT**：TXT 可像顯示行號的記事本一樣編輯，MD 則像套用格式的文件一樣寫作，並支援以多種編碼開啟與儲存。
- **對中、日、韓輸入法友善**：輸入中文、日文或韓文時，文字不會破字，游標也不會亂跳。
- **保護寫好的內容不遺失**：即使未儲存就意外關閉，下次啟動時也能復原；即使在儲存過程中發生錯誤，也會保護原始檔案不受損壞。
- **免費開放原始碼**：以 GPL v3 授權條款釋出，任何人都可以自由使用與修改。

![MDNote](screenshots/zh-TW.png)

![輸入示範](screenshots/demo.gif)

## 下載

[下載最新版本 (Releases)](https://github.com/Lahai-Roi/MDNote/releases/latest)

- `MDNote-Setup-2.0.1.exe`：安裝檔。可在安裝的第一個畫面選擇語言。
- `MDNote-2.0.1-portable.zip`：免安裝可攜版。解壓縮後直接執行 `MDNote.exe`。
- 需要 Windows 10/11 64 位元與 .NET Framework 4.8（Windows 內建）。
- 由於執行檔沒有數位簽章，首次執行時 Windows 可能會跳出警告，請點選 **其他資訊 → 仍要執行**。

## 主要功能

- **所見即所得編輯**：隱藏 `#`、`**` 等符號，直接以呈現的版面寫作。若想邊看符號邊寫，可切換為**即時模式**（僅游標所在行顯示原始碼）或**原始碼**編輯。
- **格式工具列**：透過按鈕直接插入標題、粗體、斜體、刪除線、文字顏色、清單與核取方塊清單、引言、插入程式碼（單行與段落）、連結、圖片與表格。
- **表格編輯**：直接在儲存格內輸入，並使用工具列新增或刪除行列、調整對齊方式。
- **TXT 模式**：顯示行號的純文字編輯模式。將 MD 轉為 TXT 時，會移除格式符號，只留下畫面上看得到的文字。
- **分頁、視窗與大綱**：支援以分頁或新視窗開啟多份文件，並透過標題清單快速跳轉。
- **尋找與取代、列印、編碼**：支援以 UTF-8、UTF-16、CP949、Shift JIS、GBK、Big5 等編碼開啟與儲存。
- **深色模式、字型與行距、自訂 F1~F12 快速鍵**：可依個人喜好調整顯示設定與快速鍵。
- **5 種語言**：支援 한국어、English、日本語、简体中文、繁體中文，說明與範例文件也會以相同語言開啟。

![深色模式](screenshots/en-dark.png)

## 畫面功能一覽

| | |
|---|---|
| ![即時模式](screenshots/features/en-live.png)<br>**即時模式**：僅游標所在行顯示 Markdown 符號，其餘部分皆依套用格式的版面顯示。 | ![表格編輯](screenshots/features/en-table.png)<br>**表格編輯**：直接在儲存格內輸入，並使用工具列新增行列與調整對齊。 |
| ![大綱](screenshots/features/en-outline.png)<br>**大綱**：點選標題清單即可直接跳轉到該位置。 | ![尋找與取代](screenshots/features/en-find.png)<br>**尋找與取代**：尋找文件內的文字並一次全部取代。 |
| ![TXT 模式](screenshots/features/en-txt.png)<br>**TXT 模式**：像顯示行號的記事本一樣編寫純文字。 | ![語言選擇](screenshots/features/en-lang.png)<br>**5 種語言**：選單、說明與範例文件皆會切換為所選語言。 |

## 使用方法

應用程式的**說明**選單裡有兩份文件：

- **MD檔案撰寫方法**：標題、清單、表格、圖片、程式碼的寫法與範例
- **MDNote使用說明**：選單、快速鍵與檢視設定的完整說明

## 限制

- 僅支援 Windows（Windows 10/11 64 位元）。
- 數學公式（LaTeX）、流程圖（Mermaid）等擴充語法不會轉換為圖形，而是直接顯示輸入的文字。
- 沒有將文件匯出為 HTML 的功能。若要儲存為 PDF，請在列印選單中選擇「Microsoft Print to PDF」。

## 常見問題

**儲存的檔案可以用其他程式開啟嗎？**
可以。檔案會儲存為標準 Markdown（.md）或純文字（.txt），因此在其他編輯器中也能原樣開啟。不過，像文字顏色這類標準 Markdown 沒有的格式，會以 HTML 標籤儲存。

**我想邊看 Markdown 符號邊寫作。**
開啟 **檢視 → Markdown 即時預覽** 後，只有游標所在的那一行會顯示符號。若要讓整份文件都顯示符號，請點選右上角的**原始碼**按鈕。

**緊接著粗體文字後面繼續打字，還會維持粗體嗎？**
不會。在粗體結束的位置繼續輸入會變回一般文字，而在粗體文字內側輸入則會維持粗體。

**首次執行時 Windows 跳出了警告。**
這是因為執行檔沒有數位簽章。請點選 **其他資訊 → 仍要執行**。

**可以不安裝直接使用嗎？**
可以。解壓縮 `MDNote-2.0.1-portable.zip` 後執行 `MDNote.exe` 即可。

**要如何解除安裝？**
安裝版請從 Windows 設定的應用程式清單中移除。若安裝資料夾中有您自行放入的檔案，只會留下該檔案。免安裝可攜版只需將解壓縮的資料夾刪除即可。

## 隱私權

- MDNote 不會連線至網際網路，不會將使用紀錄或文件內容傳送到任何地方。
- 為了復原未儲存的內容，編輯中的內容副本只會存放在您電腦的使用者資料夾（AppData）中，儲存或關閉後即會刪除。
- 設定（字型、語言、快速鍵等）也只會儲存在您的電腦上。

## 錯誤回報與建議

請至 [Issues](https://github.com/Lahai-Roi/MDNote/issues) 留言。若能一併提供以下資訊，將有助於我們更快修正問題：

- 做了什麼操作時（點選的選單或按鍵、輸入的文字）發生了什麼狀況，以及原本應該如何運作
- Windows 版本、使用的輸入法（韓文、日文、中文等）
- 若方便請附上畫面截圖。請勿上傳包含個人隱私的文件內容。

## 從原始碼建置

使用 Windows 內建的 .NET Framework 4.8 C# 編譯器即可建置，不需要安裝 Visual Studio。

```powershell
.\src\build.ps1
```

會在 `src` 資料夾中產生 `MDNote.exe`。

安裝檔使用 [NSIS 3](https://nsis.sourceforge.io/) 製作，詳細步驟請參考 [src/installer/README.md](src/installer/README.md)。

## 授權條款

MDNote 採用 [GNU General Public License v3.0](LICENSE) 散佈。
包含修改過的 [AvalonEdit](https://github.com/icsharpcode/AvalonEdit)（MIT 授權條款）。詳情請參閱 [THIRD_PARTY_NOTICES.txt](src/THIRD_PARTY_NOTICES.txt)。

Copyright (c) 2026 Lahai-Roi
