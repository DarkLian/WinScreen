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

## 常見問題

### AI Slop！？

這個專案純粹出於熱情，也是為了真正解決問題：借助 AI 代理的幫助，為閉源、付費的應用程式提供免費、專注的替代功能。
相容性和可靠性或許還有待檢驗，而這正是最需要社群幫忙的地方。光靠我自己使用和代理測試，沒辦法發現所有問題，
儘管 AI 修復問題又強又快。不過，如果你仍然覺得這是 AI Slop，那它就是吧。我無法也不會去改變你的看法。
去找另一個可靠的替代品就好（如果有的話）。

### 為什麼不直接用現有的成熟工具？

[PowerToys](https://github.com/microsoft/powertoys) 一直在積極維護，但它著重於功能的廣度，缺少有針對性的增強。
WinScreen 是作為專注於某些 Windows 實用功能的補充而存在，不是要複製 PowerToys，那樣毫無意義。

[WindowTop](https://windowtop.info/) 是這個專案的靈感來源。但它是閉源且付費的。
WinScreen 希望為想使用那些被付費牆鎖住的功能的人（包括我自己）提供一個免費、開源的選擇。
而且沒有商業上的束縛，WinScreen 可以輕鬆、快速地更新，所以你可以期待快速的問題修復，以及依需求快速加入的新功能
（希望還有充分的測試）。

我找到的許多其他專案，要麼已經停止維護，要麼付費，要麼就是真正的 AI Slop：不更新，我第一天發現的問題至今還開著，
GitHub 頁面也是一團糟。
**目前根本沒有一個選項，同時做到免費、開源、有針對性的增強，並且持續提供支援。**

### 這個專案會持續維護嗎？

我會盡力。不過這是一個出於熱情、不以營利為目的的專案，社群的支持對我是莫大的鼓舞。
我無法保證一直維護下去，但即便如此，它採用 MIT 授權，任何人都可以 fork 並讓它繼續活下去。
這就是開源精神！

### 安全嗎？

專案完全開源，任何人都可以檢查程式碼（或是讓 AI 幫忙分析），也可以自己建置。我的 GitHub 帳號已啟用兩步驟驗證。
如果想要更高的安全性，可以關閉自動更新，並用 AI 分析程式碼。

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
- English、简体中文與繁體中文

## 安裝

從 [Releases](https://github.com/DarkLian/WinScreen/releases) 下載 **WinScreen-…-x64-Setup.exe** 並執行。
適用於 64 位元 Intel 或 AMD 電腦（x64）上的 Windows 10（2004 版或更新版本）與 Windows 11。
其他系統的情況以及各功能能做什麼、不能做什麼，請見[功能相容性](docs/FEATURE_COMPATIBILITY.md)。

不用另外安裝任何東西，也無須系統管理員權限即可執行。WinScreen 會顯示在系統匣中。

安裝程式未經簽署，Windows 可能提示無法辨識。請點選 **其他資訊**，然後點選 **仍要執行**。
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

**WinScreen 未經簽署。**[SignPath Foundation](https://signpath.org/) 的免費程式碼簽署要求專案先有更多使用者，我們之後會再次申請。

- **參與者：**[DarkLian](https://github.com/DarkLian) 在 AI 助手的協助下撰寫和審查 WinScreen 的程式碼，並核准每個發布。
  其他人無法修改程式碼或發布新版本。
- **隱私：**WinScreen 不會把你或你電腦的任何資訊傳送到任何地方。只有在你開啟自動更新或選擇 **檢查更新** 時才會連網，
  而且只是向 GitHub 查詢是否有新版本。

## 授權

[MIT](LICENSE)
