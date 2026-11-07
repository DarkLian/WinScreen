# WinScreen

设置支持调整不透明度步长和置顶边框、可选 Ctrl+Alt+滚轮不透明度控制、深色检测及 JSON 导入/导出。
导出文件包含程序路径；滚轮不透明度控制不适用于管理员应用。详见[用户指南](docs/USER_GUIDE.zh-CN.md)。

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

免费的 Windows 窗口工具，面向 64 位 Windows 11。无需账号、试用期或激活码。

可将窗口置顶、调整透明度、打开窗口、显示器或屏幕区域的实时画中画预览、精确移动和调整窗口，或通过浮动锚点快速切换到指定窗口。
界面支持 English、简体中文和繁體中文。

## 安装

发布版本后，从 [Releases](https://github.com/DarkLian/WinScreen/releases) 下载 **WinScreen-win-Setup.exe** 并运行。
无需管理员权限，也无需另外安装 .NET。启动后，WinScreen 会在系统托盘中运行。

本地开发安装程序未进行 Authenticode 代码签名，Windows 可能显示 SmartScreen 警告。正式发布必须通过有效的发布者签名验证。请只运行你信任的安装程序。
卸载位置：**Windows 设置 → 应用 → 已安装的应用**。


## 安全默认值

当前仅在 Windows 11 Pro（内部版本 26300）、单台 1920×1080 显示器、100% 缩放下完成本地测试。Windows 10、多显示器和混合缩放仍未验证。

默认点击画中画只切换到原窗口。点击转发、深色、玻璃、裁剪、缩小、卷起和自动避让属于实验性功能，需要在设置中主动开启。锚点右键现在打开普通菜单。
按 **Ctrl+Alt+U** 还原一个窗口，按 **Ctrl+Alt+R** 还原全部；排除窗口也可还原。问题始终会以通知提示，并保留在托盘的「最近结果」中，即使关闭了操作确认也是如此。

## 快速开始

在 **设置 → 鼠标手势 → 窗口菜单** 中启用后，可用 Ctrl+右键单击操作鼠标下方的窗口。
托盘和窗口菜单还提供无边框、填满显示器、原生深色标题栏、复制及打开文件位置。
从 Alt+Tab 和任务栏隐藏 PiP 来源是实验性的单视图选项；恢复方式与应用限制请参阅指南。

1. 先激活需要操作的窗口。
2. 按 **Ctrl+Alt+T** 置顶，按 **Ctrl+Alt+P** 打开画中画，或按 **Ctrl+Alt+A** 添加浮动锚点。
3. 点击系统托盘中的 WinScreen 图标打开 **设置**，修改快捷键、语言、通知及其他选项。右键点击可使用全部命令。
4. 按 **Ctrl+Alt+R**，或在托盘菜单中选择 **还原所有窗口**，撤销 WinScreen 对窗口的更改。

将鼠标停在当前窗口标题栏的中间，也会显示工具栏。

| 功能 | 默认快捷键 |
| --- | --- |
| 置顶／取消置顶原窗口 | Ctrl+Alt+T |
| 打开／关闭小型实时画中画预览 | Ctrl+Alt+P |
| 添加／移除浮动锚点 | Ctrl+Alt+A |
| 切换默认透明度 | Ctrl+Alt+O |
| 增加／减少不透明度 | Ctrl+Alt+Up / Down |
| 开启／关闭鼠标穿透 | Ctrl+Alt+C |
| 还原所有已更改的窗口 | Ctrl+Alt+R |
| 仅还原当前窗口 | Ctrl+Alt+U |

完整快捷键、锚点操作、画中画交互、设置和故障排查，请参阅 **[用户指南](docs/USER_GUIDE.zh-CN.md)**。依赖某项功能之前，请先查看 **[已知限制](docs/USER_GUIDE.zh-CN.md#已知限制)**。

## 更新

手动升级时，先退出 WinScreen，再使用同一个 Windows 账号运行新版安装程序。通常无需先卸载旧版；设置保存在独立目录中。

自动更新需要在设置中**主动开启**。安装版会在后台检查新版本，验证更新元数据的签名及安装包的哈希值，
并在正常退出时安装，不会自动重新启动。关闭此选项会取消待处理的更新工作和安装。

自动更新需要本仓库中已签名的发布文件。正式发布前必须配置更新签名密钥、有效的预期 Authenticode 发布者签名、受保护的发布环境，并完成对应提交的专用桌面验证。
目前尚未完成两个已发布版本之间的真实升级验证。

更新元数据的签名与安装程序的 Windows 代码签名是两回事。本地开发安装程序目前仍未进行 Authenticode 签名。
信任边界和限制请参阅 **[安全说明（英文）](SECURITY.md)**。

## 分享你的使用体验

WinScreen 由作者 DarkLian 与 AI 智能体共同维护，因此最好的帮助方式不是提交 Pull Request，而是告诉我们它在你那里用起来怎么样。
欢迎[提交 Issue](https://github.com/DarkLian/WinScreen/issues) 分享使用体验、提出建议或报告问题，再小的不便也值得反馈。
更多说明请参阅 **[贡献指南（英文）](CONTRIBUTING.md)**。

如果 WinScreen 对你有帮助，也欢迎[在 Ko-fi 上支持这个项目](https://ko-fi.com/darklian)。

## 构建

需要 `global.json` 中指定的 .NET 10 SDK。

```powershell
.\build.ps1 -SkipInteractive
```

输出文件：`dist\release\WinScreen-win-Setup.exe`。

## 许可证

[MIT](LICENSE)。
