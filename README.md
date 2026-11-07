# WinScreen

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

A free window helper for 64-bit Windows 11. No account, trial, or activation key.
Locally tested on Windows 11 Pro build 26300, one 1920×1080 display at 100% scaling. Other Windows editions, Windows 10,
mixed DPI, and multiple monitors remain unverified.

Keep a window on top, adjust its transparency, open a live Picture-in-Picture view of a window, display, or screen region,
move and resize windows exactly, or jump to them with a floating Anchor.
The interface supports English, 简体中文, and 繁體中文.

Settings include adjustable opacity steps and pin borders, optional Ctrl+Alt+wheel opacity, already-dark detection,
and JSON import/export. Exported files contain program paths; the opacity wheel cannot control administrator apps.
Window views have a compact hover title bar and optional whole-view preview or reading lens. Minimized views are
restored from the tray. Hover previews are available for window sources only.

## Install

Download **WinScreen-win-Setup.exe** from [Releases](https://github.com/DarkLian/WinScreen/releases) when a release is available, then run it.
No administrator rights or separate .NET installation are needed. WinScreen runs in the system tray.

Local developer installers are not Authenticode signed, so Windows may show a SmartScreen warning. Public release publication requires a valid publisher signature. Only run an installer you trust.
Uninstall through **Windows Settings → Apps → Installed apps**.

## Safety defaults

PiP clicks bring the original window forward by default. Click forwarding, Dark, Glass, Crop, Shrink, Roll up, and auto-move
are experimental and must be explicitly enabled in Settings. Anchor right-click opens a normal context menu.
Use **Ctrl+Alt+U** to restore one window or **Ctrl+Alt+R** to restore all. Recovery remains available for excluded windows.
Problems always appear as a notification and stay in the tray's **Last result** item, even with confirmations turned off.

## Quick start

1. Activate the window you want to change.
2. Press **Ctrl+Alt+T** to pin it on top, **Ctrl+Alt+P** for Picture-in-Picture, or **Ctrl+Alt+A** for an Anchor.
3. Click WinScreen's tray icon to open **Settings** and change hotkeys, language, notifications, and other options. Right-click it for every command.
4. Press **Ctrl+Alt+R**, or choose **Restore all windows** from the tray menu, to undo WinScreen's window changes.

A toolbar also appears when the pointer rests on the middle of the active window's title bar. **Settings → Toolbar** changes its placement, delay, and buttons; opening it from the Minimize button works only on title bars drawn by Windows. **Settings → Features** lists what's available and its shortcut.

Enable **Settings → Mouse gestures → Window menu** for Ctrl+right-click commands on the window under the pointer.
The tray and window menus also offer borderless mode, display fill, a native dark title bar, Copy, and Open file location.
Hiding a PiP source from Alt+Tab and the taskbar is an experimental per-view option. See the guide for recovery and app limitations.

| Feature | Default shortcut |
| --- | --- |
| Pin / unpin the actual window | Ctrl+Alt+T |
| Open / close a small live Picture-in-Picture view | Ctrl+Alt+P |
| Add / remove a floating Anchor | Ctrl+Alt+A |
| Toggle default transparency | Ctrl+Alt+O |
| Increase / decrease opacity | Ctrl+Alt+Up / Down |
| Toggle mouse click-through | Ctrl+Alt+C |
| Restore all changed windows | Ctrl+Alt+R |
| Restore only the active window | Ctrl+Alt+U |

See the **[User Guide](docs/USER_GUIDE.md)** for every shortcut, Anchor controls, PiP interaction, settings, and troubleshooting, and see its
**[Known limitations](docs/USER_GUIDE.md#known-limitations)** before relying on a feature.

## Updates

For a manual upgrade, exit WinScreen and run the newer installer under the same Windows account. You normally do not need to uninstall first; settings are stored separately.

Automatic updates are **opt-in** in Settings. An installed copy checks in the background, verifies signed update metadata and package hashes, and installs on normal exit without restarting the app. Turning the option off cancels pending work and installation.

Automatic updates need signed releases hosted in this repository. Release signing, a valid expected Authenticode publisher, a protected release environment, and a successful dedicated desktop-validation run for this exact commit are required before publication. A live upgrade between two hosted versions has not yet been verified.

Local installers remain unsigned even though update metadata is signed. See **[Security](SECURITY.md)** for the trust model and limits.

## Share your experience

WinScreen is maintained by its author, DarkLian, together with AI agents, so the best way to help isn't a pull request.
It's telling us how WinScreen works for you. Please [open an issue](https://github.com/DarkLian/WinScreen/issues) to
share your experience, suggest an idea, or report a problem. Small annoyances count too. See **[Contributing](CONTRIBUTING.md)** for what helps most.

If WinScreen is useful to you, you can also [support it on Ko-fi](https://ko-fi.com/darklian).

## Build

Requires the .NET 10 SDK specified in `global.json`.

```powershell
.\build.ps1 -SkipInteractive
```

Output: `dist\release\WinScreen-win-Setup.exe`.

## License

[MIT](LICENSE).
