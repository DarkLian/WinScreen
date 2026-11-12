<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen 圖示">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>🧪 Beta 測試中。</strong>功能仍可能變化，部分功能仍處於實驗階段。現在最需要你的回饋。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-beta%20testing-yellow" alt="狀態：Beta 測試中">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="建置"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?include_prereleases&label=release" alt="最新版本"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 與 11">
  <img src="https://img.shields.io/badge/arch-x64-4c8bf5" alt="x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT 授權"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a>
</p>

WinScreen 是一個免費的視窗小幫手。可以讓視窗保持置頂、變成半透明、用小視窗即時查看，或一鍵找回被擋住的視窗。
無須帳號、試用期或啟用碼。

## 介面一覽

<p align="center">
  <img src="docs/assets/screenshots/settings-general.png" width="720" alt="WinScreen 設定的「一般」頁面（深色）">
</p>
<p align="center">
  <img src="docs/assets/screenshots/window-menu.png" width="360" alt="視窗選單，已開啟「效果」子選單">
  <img src="docs/assets/screenshots/settings-appearance.png" width="440" alt="設定的「外觀」頁面，用滑桿調整不透明度">
</p>

## 透過小檢視使用應用程式

在視窗上按 **Ctrl+Alt+P**，即可開啟一個置頂的小即時檢視。在檢視選單中開啟**互動** *（實驗性）*，就能直接在檢視裡使用應用程式：
點選、輸入、捲動、選取文字和開啟選單。真正的視窗會隱藏起來，讓影片、聊天或文件一直待在角落，方便你邊看邊做別的事。

**增強懸停**讓瀏覽器以及 VS Code、Discord 等應用程式的懸停效果正常顯示。對於不適合透過檢視使用的應用程式（例如遊戲），
點選檢視可改為顯示原視窗，切換到別的視窗後它會再次隱藏。詳情請見[功能相容性](docs/FEATURE_COMPATIBILITY.md)。

## 功能

**視窗**
- 置頂，可加彩色邊框
- 半透明：預設透明度、Ctrl+Alt+滾輪調整指標下的視窗、滑鼠停留時恢復不透明
- 滑鼠穿透
- 無邊框、填滿顯示器、深色標題列
- 記住程式的設定，之後它的視窗都照此開啟

**子母畫面**
- 為視窗、整個顯示器或螢幕的一部分開啟即時檢視
- 裁切、縮放、調整大小、預設和已儲存的來源
- 隱藏原視窗並保持檢視更新
- 透過檢視使用應用程式，支援增強懸停與中鍵捲動 *（實驗性）*
- 每個檢視單獨設定畫面更新上限、不透明度、鎖定位置與滑鼠穿透
- 把檢視最小化到工作列、停留預覽與閱讀放大鏡
- 自動避開文字與圖片的檢視 *（實驗性）*

**錨點**
- 浮動按鈕，點一下就把視窗帶回來
- 可把檔案拖到上面，指標停留時顯示即時預覽
- 視窗被擋住時自動新增和移除
- 可把對應視窗從工作列移除，或暫時隱藏所有錨點

**排列**
- 螢幕上的九個位置、下一台顯示器、精確的位置與大小、上一個位置
- 按住 Alt 或 Win 拖曳即可移動和調整大小，並可貼齊螢幕邊緣
- 收起到標題列 *（實驗性）*、移到最底層

**效果** *（實驗性）*
- 深色模式，以及保持圖片與影片原樣的版本
- 只淡化背景的玻璃效果
- 裁切視窗，或縮小成即時小圖塊

**日常使用**
- 標題列工具列、Ctrl+右鍵視窗選單和完整的系統匣選單
- 可修改的快捷鍵，以及可搜尋的全部動作清單
- 與 Windows 11 一致的設定介面，支援淺色與深色
- 指定不處理的程式，或只處理哪些程式
- 遊戲與全螢幕應用程式執行時自動暫停
- 需要時以系統管理員身分執行，用於工作管理員等應用程式
- 按 Ctrl+Alt+R 恢復一切；即使 WinScreen 意外停止，視窗也會恢復
- 自動更新，設定可移轉到另一台電腦
- 在系統匣中待命時只佔用約 20 MB 記憶體，幾乎不佔處理器
- English、简体中文與繁體中文

## 安裝

從 [Releases](https://github.com/DarkLian/WinScreen/releases) 下載 **WinScreen-…-x64-Setup.exe** 並執行。
適用於 64 位元 Intel 或 AMD 電腦（x64）上的 Windows 10（2004 版或更新版本）與 Windows 11。
其他系統的情況以及各功能能做什麼、不能做什麼，請見[功能相容性](docs/FEATURE_COMPATIBILITY.md)。

不用另外安裝任何東西，也無須系統管理員權限即可執行。WinScreen 會顯示在系統匣中。

安裝程式尚未簽署，Windows 可能提示無法辨識。請點選 **其他資訊**，然後點選 **仍要執行**。
如需解除安裝，請開啟 Windows 的 **設定 → 應用程式 → 已安裝的應用程式**。

## 快速上手

1. 點選要變更的視窗。
2. 按 **Ctrl+Alt+T** 置頂，按 **Ctrl+Alt+P** 開啟即時檢視，或按 **Ctrl+Alt+A** 新增錨點。
3. 點選系統匣圖示開啟設定，以滑鼠右鍵點選可使用所有命令。
4. 按 **Ctrl+Alt+R** 把所有視窗恢復原樣。

| 操作 | 快捷鍵 |
| --- | --- |
| 置頂或取消置頂 | Ctrl+Alt+T |
| 開啟或關閉即時檢視 | Ctrl+Alt+P |
| 新增或移除錨點 | Ctrl+Alt+A |
| 更透明或更不透明 | Ctrl+Alt+Down / Up |
| 開關預設透明度 | Ctrl+Alt+O |
| 開關滑鼠穿透 | Ctrl+Alt+C |
| 還原此視窗 | Ctrl+Alt+U |
| 還原所有視窗 | Ctrl+Alt+R |

**[使用指南](docs/USER_GUIDE.zh-TW.md)** 介紹了所有功能。

## 更新

在設定中開啟自動更新後，WinScreen 會在關閉時安裝新版本。手動更新時，先關閉 WinScreen，再執行新版安裝程式。
你的設定會保留。

## 告訴我們你的想法

WinScreen 由 DarkLian 在 AI 助手的協助下開發。最好的幫助就是告訴我們它用起來怎麼樣。
歡迎 [提交 issue](https://github.com/DarkLian/WinScreen/issues) 分享想法、回報問題，或說說讓你不順手的小地方。
**[參與貢獻](CONTRIBUTING.md)** 說明了哪些回饋最有幫助。

如果 WinScreen 對你有用，也可以在 [Ko-fi](https://ko-fi.com/darklian) 上支持它。

## 自行建置

需要 `global.json` 中指定的 .NET 10 SDK。執行 `.\build.ps1 -SkipInteractive`，安裝程式會出現在 `dist\release`。

## 程式碼簽署政策

**WinScreen 尚未簽署。**我們已向 [SignPath Foundation](https://signpath.org/) 申請免費程式碼簽署，正在等待回覆。
獲准後，發布版本將會簽署：免費程式碼簽署由 [SignPath.io](https://about.signpath.io/) 提供，憑證來自 SignPath Foundation。

- **參與者：**[DarkLian](https://github.com/DarkLian) 在 AI 助手的協助下撰寫和審查 WinScreen 的程式碼，並核准每個簽署發布。
  其他人無法修改程式碼或簽署發布。
- **隱私：**WinScreen 不會把你或你電腦的任何資訊傳送到任何地方。只有在你開啟自動更新或選擇 **檢查更新** 時才會連網，
  而且只是向 GitHub 查詢是否有新版本。

## 授權

[MIT](LICENSE)
