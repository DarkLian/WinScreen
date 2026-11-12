<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen 图标">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>🧪 Beta 测试中。</strong>功能仍可能变化，部分功能仍处于实验阶段。现在最需要你的反馈。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-beta%20testing-yellow" alt="状态：Beta 测试中">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="构建"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?include_prereleases&label=release" alt="最新版本"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 和 11">
  <img src="https://img.shields.io/badge/arch-x64-4c8bf5" alt="x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT 许可证"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a>
</p>

WinScreen 是一个免费的窗口小帮手。可以让窗口保持置顶、变成半透明、用小窗口实时查看，或一键找回被挡住的窗口。
无需账号、试用期或激活码。

## 界面一览

<p align="center">
  <img src="docs/assets/screenshots/settings-general.png" width="720" alt="WinScreen 设置的“常规”页面（深色）">
</p>
<p align="center">
  <img src="docs/assets/screenshots/window-menu.png" width="360" alt="窗口菜单，已打开“效果”子菜单">
  <img src="docs/assets/screenshots/settings-appearance.png" width="440" alt="设置的“外观”页面，用滑块调整不透明度">
</p>

## 通过小视图使用应用

在窗口上按 **Ctrl+Alt+P**，即可打开一个置顶的小实时视图。在视图菜单中开启**交互** *（实验性）*，就能直接在视图里使用应用：
点击、输入、滚动、选取文字和打开菜单。真正的窗口会隐藏起来，让视频、聊天或文档一直待在角落，方便你边看边做别的事。

**增强悬停**让浏览器以及 VS Code、Discord 等应用的悬停效果正常显示。对于不适合通过视图使用的应用（例如游戏），
点击视图可改为显示原窗口，切换到别的窗口后它会再次隐藏。详情见[功能兼容性](docs/FEATURE_COMPATIBILITY.md)。

## 功能

**窗口**
- 置顶，可加彩色边框
- 半透明：默认透明度、Ctrl+Alt+滚轮调整指针下的窗口、鼠标悬停时恢复不透明
- 鼠标穿透
- 无边框、填满显示器、深色标题栏
- 记住程序的配置，以后它的窗口都照此打开

**画中画**
- 为窗口、整个显示器或屏幕的一部分打开实时视图
- 裁剪、缩放、调整大小、预设和已保存的来源
- 隐藏原窗口并保持视图更新
- 通过视图使用应用，支持增强悬停和中键滚动 *（实验性）*
- 每个视图单独设置帧率上限、不透明度、锁定位置和鼠标穿透
- 把视图最小化到任务栏、悬停预览和阅读放大镜
- 自动避开文字和图片的视图 *（实验性）*

**锚点**
- 悬浮按钮，点一下就把窗口带回来
- 可把文件拖到上面，指针停留时显示实时预览
- 窗口被挡住时自动添加和移除
- 可把对应窗口从任务栏移除，或暂时隐藏所有锚点

**排列**
- 屏幕上的九个位置、下一个显示器、精确的位置和大小、上一个位置
- 按住 Alt 或 Win 拖动即可移动和调整大小，并可贴靠屏幕边缘
- 收起到标题栏 *（实验性）*、移到最底层

**效果** *（实验性）*
- 深色模式，以及保持图片和视频原样的版本
- 只淡化背景的玻璃效果
- 裁剪窗口，或缩小成实时小图块

**日常使用**
- 标题栏工具栏、Ctrl+右键窗口菜单和完整的托盘菜单
- 可修改的快捷键，以及可搜索的全部操作列表
- 与 Windows 11 一致的设置界面，支持浅色和深色
- 指定不处理的程序，或只处理哪些程序
- 游戏和全屏应用运行时自动暂停
- 需要时以管理员身份运行，用于任务管理器等应用
- 按 Ctrl+Alt+R 恢复一切；即使 WinScreen 意外停止，窗口也会恢复
- 自动更新，设置可迁移到另一台电脑
- 在托盘中待命时只占用约 20 MB 内存，几乎不占处理器
- English、简体中文和繁體中文

## 安装

从 [Releases](https://github.com/DarkLian/WinScreen/releases) 下载 **WinScreen-…-x64-Setup.exe** 并运行。
适用于 64 位 Intel 或 AMD 电脑（x64）上的 Windows 10（2004 版或更高版本）和 Windows 11。
其他系统的情况以及各功能能做什么、不能做什么，请见[功能兼容性](docs/FEATURE_COMPATIBILITY.md)。

不用另外安装任何东西，也无需管理员权限即可运行。WinScreen 会显示在系统托盘中。

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

## 代码签名政策

**WinScreen 暂未签名。**我们已向 [SignPath Foundation](https://signpath.org/) 申请免费代码签名，正在等待回复。
获批后，发布版本将会签名：免费代码签名由 [SignPath.io](https://about.signpath.io/) 提供，证书来自 SignPath Foundation。

- **参与者：**[DarkLian](https://github.com/DarkLian) 在 AI 助手的协助下编写和审查 WinScreen 的代码，并批准每个签名发布。
  其他人无法修改代码或签署发布。
- **隐私：**WinScreen 不会把你或你电脑的任何信息发送到任何地方。只有在你开启自动更新或选择 **检查更新** 时才会联网，
  而且只是向 GitHub 查询是否有新版本。

## 许可证

[MIT](LICENSE)
