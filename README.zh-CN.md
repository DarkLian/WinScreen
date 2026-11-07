# WinScreen

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

免费的 Windows 窗口工具，支持 64 位 Windows 10（2004 及以上）和 Windows 11。无需账号、试用期或激活码。

可将窗口置顶、调整透明度、打开实时画中画预览，或通过浮动锚点快速切换到指定窗口。
界面支持 English、简体中文和繁體中文。

## 安装

发布版本后，从 [Releases](https://github.com/DarkLian/WinScreen/releases) 下载 **WinScreen-win-Setup.exe** 并运行。
无需管理员权限，也无需另外安装 .NET。启动后，WinScreen 会在系统托盘中运行。

安装程序尚未进行 Authenticode 代码签名，Windows 可能显示 SmartScreen 警告。请只运行你信任的安装程序。
卸载位置：**Windows 设置 → 应用 → 已安装的应用**。

## 快速开始

1. 先激活需要操作的窗口。
2. 按 **Ctrl+Alt+T** 置顶，按 **Ctrl+Alt+P** 打开画中画，或按 **Ctrl+Alt+A** 添加浮动锚点。
3. 右键点击系统托盘中的 WinScreen 图标，打开 **设置**，修改快捷键、语言、通知及其他选项。
4. 按 **Ctrl+Alt+R**，或在托盘菜单中选择 **还原所有窗口**，撤销 WinScreen 对窗口的更改。

将鼠标停在当前窗口标题栏的中间，也会显示工具栏。

| 功能 | 默认快捷键 |
| --- | --- |
| 置顶／取消置顶原窗口 | Ctrl+Alt+T |
| 打开小型实时画中画预览 | Ctrl+Alt+P |
| 添加／移除浮动锚点 | Ctrl+Alt+A |
| 切换默认透明度 | Ctrl+Alt+O |
| 增加／减少不透明度 | Ctrl+Alt+Up / Down |
| 开启／关闭鼠标穿透 | Ctrl+Alt+C |
| 还原所有已更改的窗口 | Ctrl+Alt+R |

完整快捷键、锚点操作、画中画交互、设置和故障排查，请参阅 **[用户指南](docs/USER_GUIDE.zh-CN.md)**。

## 更新

手动升级时，先退出 WinScreen，再使用同一个 Windows 账号运行新版安装程序。通常无需先卸载旧版；设置保存在独立目录中。

自动更新需要在设置中**主动开启**。安装版会在后台检查新版本，验证更新元数据的签名及安装包的哈希值，
并在正常退出时安装，不会自动重新启动。关闭此选项会取消待处理的更新工作和安装。

自动更新需要本仓库中已签名的发布文件。首次通过工作流发布前，必须先配置更新签名密钥 Secret。
目前尚未完成两个已发布版本之间的真实升级验证。

更新元数据的签名与安装程序的 Windows 代码签名是两回事。安装程序目前仍未进行 Authenticode 签名。
信任边界和限制请参阅 **[安全说明（英文）](SECURITY.md)**。

## 构建与贡献

需要 `global.json` 中指定的 .NET 10 SDK。

```powershell
.\build.ps1 -SkipInteractive
```

输出文件：`dist\release\WinScreen-win-Setup.exe`。
测试、项目结构、发布签名和已知限制请参阅 **[开发指南（英文）](docs/DEVELOPMENT.md)**。
**[公开文件清单（英文）](docs/PUBLIC_FILES.md)** 列出了准备提交到 GitHub 的源文件；安装程序通过 Releases 分发，不提交到源码中。

## 许可证

[MIT](LICENSE)。
