<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen 图标">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>🚧 正在积极开发中。</strong>功能变化较快，部分功能仍处于实验阶段。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-under%20heavy%20development-orange" alt="状态：积极开发中">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="构建"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?include_prereleases&label=release" alt="最新版本"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 和 11">
  <img src="https://img.shields.io/badge/arch-x64%20%7C%20ARM64-4c8bf5" alt="x64 和 ARM64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT 许可证"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a>
</p>

WinScreen 是一个免费的窗口小帮手。可以让窗口保持置顶、变成半透明、用小窗口实时查看，或一键找回被挡住的窗口。
无需账号、试用期或激活码。

## 亮点

**让窗口待在需要的地方**
- 把任意窗口置顶，还能设为半透明或鼠标穿透。
- 按住 Ctrl+Alt 滚动滚轮，直接调整指针下窗口的不透明度。
- 记住程序的配置，让它的窗口每次打开都置顶、半透明或深色。

**画中画**
- 为窗口、整个显示器或屏幕的一部分打开小的实时视图。
- 可以裁剪、放大、调整大小，并把布局保存为预设。
- 隐藏原窗口并保持视图更新，把视图最小化到任务栏，悬停即可预览。
- 直接在视图里点击和滚动 *（实验性）*。

**锚点**
- 悬浮按钮，一键把窗口带回前台。
- 窗口被其他窗口挡住时，也可以自动添加。

**排列窗口**
- 把窗口放到九个位置之一、移到下一个显示器，或输入精确的位置和大小。
- 按住一个键拖动窗口任意处即可移动或调整大小。
- 无边框模式、填满显示器、收起到标题栏、移到最底层。

**效果** *（实验性）*
- 深色模式，以及保持图片和视频原样的版本。
- 只淡化背景、保留文字和图片的玻璃效果。
- 把窗口裁剪到需要的部分，或缩小成实时小图块。

**好用，放心试**
- 标题栏工具栏、鼠标菜单，快捷键都可以修改。
- 按 Ctrl+Alt+R 撤销所有更改。如果 WinScreen 意外停止，下次启动时窗口会恢复原样。
- 轻巧：在托盘中待命时只占用约 20 MB 内存，几乎不占处理器。
- 支持 English、简体中文和繁體中文。

## 安装

从 [Releases](https://github.com/DarkLian/WinScreen/releases) 下载安装程序并运行：

- **WinScreen-win-Setup.exe**：适用于大多数电脑上的 Windows 10（2004 版或更高版本）和 Windows 11。
- **WinScreen-win-arm64-Setup.exe**：适用于 ARM 电脑上的 Windows 11。

无需管理员权限，也不用另外安装任何东西。WinScreen 会显示在系统托盘中。

安装程序暂未签名，Windows 可能提示无法识别。请点击 **更多信息**，然后点击 **仍要运行**。
如需卸载，请打开 Windows 的 **设置 → 应用 → 已安装的应用**。

## 快速上手

1. 点击要更改的窗口。
2. 按 **Ctrl+Alt+T** 置顶，按 **Ctrl+Alt+P** 打开实时视图，或按 **Ctrl+Alt+A** 添加锚点。
3. 点击托盘图标打开设置，右键点击可使用所有命令。
4. 按 **Ctrl+Alt+R** 把所有窗口恢复原样。

| 操作 | 快捷键 |
| --- | --- |
| 置顶或取消置顶 | Ctrl+Alt+T |
| 打开或关闭实时视图 | Ctrl+Alt+P |
| 添加或移除锚点 | Ctrl+Alt+A |
| 更透明或更不透明 | Ctrl+Alt+Down / Up |
| 开关默认透明度 | Ctrl+Alt+O |
| 开关鼠标穿透 | Ctrl+Alt+C |
| 还原此窗口 | Ctrl+Alt+U |
| 还原所有窗口 | Ctrl+Alt+R |

**[用户指南](docs/USER_GUIDE.zh-CN.md)** 介绍了所有功能。

## 更新

在设置中开启自动更新后，WinScreen 会在关闭时安装新版本。手动更新时，先关闭 WinScreen，再运行新版安装程序。
你的设置会保留。

## 告诉我们你的想法

WinScreen 由 DarkLian 在 AI 助手的协助下开发。最好的帮助就是告诉我们它用起来怎么样。
欢迎 [提交 issue](https://github.com/DarkLian/WinScreen/issues) 分享想法、报告问题，或说说让你不顺手的小地方。
**[参与贡献](CONTRIBUTING.md)** 说明了哪些反馈最有帮助。

如果 WinScreen 对你有用，也可以在 [Ko-fi](https://ko-fi.com/darklian) 上支持它。

## 自行构建

需要 `global.json` 中指定的 .NET 10 SDK。运行 `.\build.ps1 -SkipInteractive`，安装程序会出现在 `dist\release`。

## 许可证

[MIT](LICENSE)
