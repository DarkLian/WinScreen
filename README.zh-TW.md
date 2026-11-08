<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen 圖示">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>🚧 正在積極開發中。</strong>功能變化較快，部分功能仍處於實驗階段。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-under%20heavy%20development-orange" alt="狀態：積極開發中">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="建置"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?include_prereleases&label=release" alt="最新版本"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 與 11">
  <img src="https://img.shields.io/badge/arch-x64%20%7C%20ARM64-4c8bf5" alt="x64 與 ARM64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT 授權"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a>
</p>

WinScreen 是一個免費的視窗小幫手。可以讓視窗保持置頂、變成半透明、用小視窗即時查看，或一鍵找回被擋住的視窗。
無須帳號、試用期或啟用碼。

## 亮點

**讓視窗待在需要的地方**
- 把任何視窗置頂，還能設為半透明或滑鼠穿透。
- 按住 Ctrl+Alt 捲動滾輪，直接調整指標下視窗的不透明度。
- 記住程式的設定，讓它的視窗每次開啟都置頂、半透明或深色。

**子母畫面**
- 為視窗、整個顯示器或螢幕的一部分開啟小的即時檢視。
- 可以裁切、放大、調整大小，並把版面儲存為預設。
- 隱藏原視窗並保持檢視更新，把檢視最小化到工作列，停留即可預覽。
- 直接在檢視裡點選和捲動 *（實驗性）*。

**錨點**
- 浮動按鈕，一鍵把視窗帶回前景。
- 視窗被其他視窗擋住時，也可以自動新增。

**排列視窗**
- 把視窗放到九個位置之一、移到下一台顯示器，或輸入精確的位置與大小。
- 按住一個鍵拖曳視窗任何地方即可移動或調整大小。
- 無邊框模式、填滿顯示器、收起到標題列、移到最底層。

**效果** *（實驗性）*
- 深色模式，以及保持圖片與影片原樣的版本。
- 只淡化背景、保留文字與圖片的玻璃效果。
- 把視窗裁切到需要的部分，或縮小成即時小圖塊。

**好用，放心試**
- 標題列工具列、功能頁、滑鼠選單，快捷鍵都可以修改。
- 按 Ctrl+Alt+R 復原所有變更。即使 WinScreen 意外停止，視窗也會自動恢復。
- 輕巧：在系統匣中待命時只佔用約 16 MB 記憶體。
- 支援 English、简体中文與繁體中文。

## 安裝

從 [Releases](https://github.com/DarkLian/WinScreen/releases) 下載安裝程式並執行：

- **WinScreen-win-Setup.exe**：適用於大多數電腦上的 Windows 10（2004 版或更新版本）與 Windows 11。
- **WinScreen-win-arm64-Setup.exe**：適用於 ARM 電腦上的 Windows 11。

無須系統管理員權限，也不用另外安裝任何東西。WinScreen 會顯示在系統匣中。
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

## 授權

[MIT](LICENSE)
