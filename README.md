# WinScreen

[English](README.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

A free window helper for 64-bit Windows 11. No account, trial, or activation key.
Locally tested on Windows 11 Pro build 26300, one 1920×1080 display at 100% scaling. Other Windows editions, Windows 10,
mixed DPI, and multiple monitors remain unverified; see the [validation matrix](docs/AUDIT_IMPLEMENTATION.md).

Keep a window on top, adjust its transparency, open a live Picture-in-Picture view, or jump to it with a floating Anchor.
The interface supports English, 简体中文, and 繁體中文.

## Install

Download **WinScreen-win-Setup.exe** from [Releases](https://github.com/DarkLian/WinScreen/releases) when a release is available, then run it.
No administrator rights or separate .NET installation are needed. WinScreen runs in the system tray.

Local developer installers are not Authenticode signed, so Windows may show a SmartScreen warning. Public release publication requires a valid publisher signature. Only run an installer you trust.
Uninstall through **Windows Settings → Apps → Installed apps**.

## Safety defaults

PiP clicks bring the original window forward by default. Click forwarding, Dark, Glass, Crop, Shrink, and auto-move
are experimental and must be explicitly enabled in Settings. Anchor right-click opens a normal context menu.
Use **Ctrl+Alt+U** to restore one window or **Ctrl+Alt+R** to restore all. Recovery remains available for excluded windows.
Problems always appear as a notification and stay in the tray's **Last result** item, even with confirmations turned off.

## Quick start

1. Activate the window you want to change.
2. Press **Ctrl+Alt+T** to pin it on top, **Ctrl+Alt+P** for Picture-in-Picture, or **Ctrl+Alt+A** for an Anchor.
3. Click WinScreen's tray icon to open **Settings** and change hotkeys, language, notifications, and other options. Right-click it for every command.
4. Press **Ctrl+Alt+R**, or choose **Restore all windows** from the tray menu, to undo WinScreen's window changes.

A toolbar also appears when the pointer rests on the middle of the active window's title bar.

| Feature | Default shortcut |
| --- | --- |
| Pin / unpin the actual window | Ctrl+Alt+T |
| Open a small live Picture-in-Picture view | Ctrl+Alt+P |
| Add / remove a floating Anchor | Ctrl+Alt+A |
| Toggle default transparency | Ctrl+Alt+O |
| Increase / decrease opacity | Ctrl+Alt+Up / Down |
| Toggle mouse click-through | Ctrl+Alt+C |
| Restore all changed windows | Ctrl+Alt+R |
| Restore only the active window | Ctrl+Alt+U |

See the **[User Guide](docs/USER_GUIDE.md)** for every shortcut, Anchor controls, PiP interaction, settings, and troubleshooting.

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
See the **[Development Guide](docs/DEVELOPMENT.md)** for tests, architecture, release signing, and known limitations.

## License

[MIT](LICENSE).
