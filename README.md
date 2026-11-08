<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen icon">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>🚧 Under heavy development.</strong> Things change quickly and some features are still experimental.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-under%20heavy%20development-orange" alt="Status: under heavy development">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?include_prereleases&label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/arch-x64%20%7C%20ARM64-4c8bf5" alt="x64 and ARM64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT license"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a>
</p>

WinScreen is a free helper for the windows on your screen. Keep a window on top, make it see-through, watch it in a small
live view, or bring a buried window back with one click. No account, no trial, no activation key.

## Highlights

**Keep windows where you need them**
- Pin any window on top, and make it see-through or click-through.
- Scroll with Ctrl+Alt to change a window's opacity right under the pointer.
- Remember a program's setup, so its windows open pinned, see-through or dark every time.

**Picture-in-Picture**
- Open a small live view of a window, a whole display, or part of the screen.
- Crop it, zoom in, resize it, and save the layout as a preset.
- Hide the original and keep the view updating, minimize views to the taskbar, and preview them on hover.
- Use a view directly, clicking and scrolling inside it *(experimental)*.

**Anchors**
- Floating buttons that bring a window back instantly.
- Added for you when a window gets buried under others, if you like.

**Arrange windows**
- Snap a window to nine places, move it to the next display, or type an exact position and size.
- Move and resize from anywhere by holding a key and dragging.
- Borderless mode, fill the display, roll up to the title bar, send to the back.

**Effects** *(experimental)*
- Dark mode, and a version that keeps pictures and video as they are.
- Glass that fades the background but keeps text and pictures.
- Crop a window to the part you need, or shrink it to a small live tile.

**Easy to use and safe to try**
- A toolbar on title bars, a Features page, a mouse menu, and hotkeys you can change.
- Undo everything with Ctrl+Alt+R. Windows come back on their own even if WinScreen stops unexpectedly.
- English, 简体中文 and 繁體中文.

## Install

Download the installer from [Releases](https://github.com/DarkLian/WinScreen/releases) and run it:

- **WinScreen-win-Setup.exe** for Windows 10 (version 2004 or later) and Windows 11 on most PCs.
- **WinScreen-win-arm64-Setup.exe** for Windows 11 on ARM.

You don't need administrator rights or anything else installed. WinScreen lives in the system tray.
To remove it, go to **Settings → Apps → Installed apps** in Windows.

## Quick start

1. Click the window you want to change.
2. Press **Ctrl+Alt+T** to pin it on top, **Ctrl+Alt+P** for a live view, or **Ctrl+Alt+A** for an Anchor.
3. Click the tray icon for Settings, or right-click it for every command.
4. Press **Ctrl+Alt+R** to put every window back the way it was.

| Do this | Shortcut |
| --- | --- |
| Pin or unpin on top | Ctrl+Alt+T |
| Open or close a live view | Ctrl+Alt+P |
| Add or remove an Anchor | Ctrl+Alt+A |
| More or less see-through | Ctrl+Alt+Down / Up |
| Switch default see-through on or off | Ctrl+Alt+O |
| Click-through on or off | Ctrl+Alt+C |
| Put this window back | Ctrl+Alt+U |
| Put every window back | Ctrl+Alt+R |

The **[User Guide](docs/USER_GUIDE.md)** explains every feature.

## Updates

Turn on automatic updates in Settings and WinScreen installs new versions when you close it. To update by hand, close
WinScreen and run the newer installer. Your settings stay as they are.

## Tell us what you think

WinScreen is made by DarkLian with the help of AI agents. The best way to help is to tell us how it works for you.
[Open an issue](https://github.com/DarkLian/WinScreen/issues) to share an idea, report a problem, or mention something
small that bugs you. See **[Contributing](CONTRIBUTING.md)** for what helps most.

If WinScreen is useful to you, you can [support it on Ko-fi](https://ko-fi.com/darklian).

## Build it yourself

You need the .NET 10 SDK named in `global.json`. Run `.\build.ps1 -SkipInteractive`, and the installer appears in `dist\release`.

## License

[MIT](LICENSE)
