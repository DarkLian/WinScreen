# WinScreen

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

免費的 Windows 視窗工具，適用於 64 位元 Windows 11。無須帳號、試用期或啟用碼。

可將視窗置頂、調整透明度、開啟即時子母畫面預覽，或透過浮動錨點快速切換至指定視窗。
介面支援 English、简体中文與繁體中文。

## 安裝

發行版本後，從 [Releases](https://github.com/DarkLian/WinScreen/releases) 下載 **WinScreen-win-Setup.exe** 並執行。
無須系統管理員權限，也無須另外安裝 .NET。啟動後，WinScreen 會在系統匣中執行。

本機開發安裝程式未進行 Authenticode 程式碼簽章，Windows 可能顯示 SmartScreen 警告。正式發行必須通過有效的發行者簽章驗證。請只執行你信任的安裝程式。
解除安裝位置：**Windows 設定 → 應用程式 → 已安裝的應用程式**。


## 安全預設值

目前僅在 Windows 11 Pro（組建 26300）、單台 1920×1080 螢幕、100% 縮放下完成本機測試。Windows 10、多螢幕與混合縮放仍未驗證。

預設點選子母畫面只切換至原視窗。點擊轉送、深色、玻璃、裁切、縮小與自動避讓屬於實驗性功能，需要在設定中主動開啟。錨點右鍵現在開啟一般選單。
按 **Ctrl+Alt+U** 還原一個視窗，按 **Ctrl+Alt+R** 還原全部；排除視窗也可還原。問題一律會以通知提示，並保留在系統匣的「最近結果」中，即使關閉了操作確認也是如此。

## 快速開始

1. 先啟用需要操作的視窗。
2. 按 **Ctrl+Alt+T** 置頂，按 **Ctrl+Alt+P** 開啟子母畫面，或按 **Ctrl+Alt+A** 新增浮動錨點。
3. 點選系統匣中的 WinScreen 圖示開啟 **設定**，修改快捷鍵、語言、通知與其他選項。以滑鼠右鍵點選可使用全部命令。
4. 按 **Ctrl+Alt+R**，或在系統匣選單中選擇 **還原所有視窗**，復原 WinScreen 對視窗的變更。

將滑鼠停在目前視窗標題列的中間，也會顯示工具列。

| 功能 | 預設快捷鍵 |
| --- | --- |
| 置頂／取消置頂原視窗 | Ctrl+Alt+T |
| 開啟／關閉小型即時子母畫面預覽 | Ctrl+Alt+P |
| 新增／移除浮動錨點 | Ctrl+Alt+A |
| 切換預設透明度 | Ctrl+Alt+O |
| 增加／減少不透明度 | Ctrl+Alt+Up / Down |
| 開啟／關閉滑鼠穿透 | Ctrl+Alt+C |
| 還原所有已變更的視窗 | Ctrl+Alt+R |
| 僅還原目前視窗 | Ctrl+Alt+U |

完整快捷鍵、錨點操作、子母畫面互動、設定與疑難排解，請參閱 **[使用指南](docs/USER_GUIDE.zh-TW.md)**。依賴某項功能之前，請先查看 **[已知限制](docs/USER_GUIDE.zh-TW.md#已知限制)**。

## 更新

手動升級時，先結束 WinScreen，再使用同一個 Windows 帳號執行新版安裝程式。通常無須先移除舊版；設定儲存在獨立目錄中。

自動更新需要在設定中**主動開啟**。安裝版會在背景檢查新版本，驗證更新中繼資料的簽章與套件的雜湊值，
並在正常結束時安裝，不會自動重新啟動。關閉此選項會取消待處理的更新工作與安裝。

自動更新需要本儲存庫中已簽章的發行檔案。正式發行前必須設定更新簽章金鑰、有效的預期 Authenticode 發行者簽章、受保護的發行環境，並完成對應提交的專用桌面驗證。
目前尚未完成兩個已發行版本之間的實際升級驗證。

更新中繼資料的簽章與安裝程式的 Windows 程式碼簽章是兩回事。本機開發安裝程式目前仍未進行 Authenticode 簽章。
信任邊界與限制請參閱 **[安全說明（英文）](SECURITY.md)**。

## 分享您的使用體驗

WinScreen 由作者 DarkLian 與 AI 代理程式共同維護，因此最好的幫助方式不是提交 Pull Request，而是告訴我們它在您那裡用起來如何。
歡迎[提交 Issue](https://github.com/DarkLian/WinScreen/issues) 分享使用體驗、提出建議或回報問題，再小的不便也值得回饋。
更多說明請參閱 **[貢獻指南（英文）](CONTRIBUTING.md)**。

如果 WinScreen 對您有幫助，也歡迎[在 Ko-fi 上支持這個專案](https://ko-fi.com/darklian)。

## 建置

需要 `global.json` 中指定的 .NET 10 SDK。

```powershell
.\build.ps1 -SkipInteractive
```

輸出檔案：`dist\release\WinScreen-win-Setup.exe`。

## 授權條款

[MIT](LICENSE)。
