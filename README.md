<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen icon">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>🧪 Beta testing.</strong> Things may still change and some features are still experimental. Your feedback helps most right now.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-beta%20testing-yellow" alt="Status: beta testing">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?include_prereleases&label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/arch-x64-4c8bf5" alt="x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT license"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a>
</p>

WinScreen is a free helper for the windows on your screen. Keep a window on top, make it see-through, watch it in a small
live view, or bring a buried window back with one click. No account, no trial, no activation key.

## A look

<p align="center">
  <img src="docs/assets/screenshots/settings-general.png" width="720" alt="WinScreen Settings, General page, in the dark look">
</p>
<p align="center">
  <img src="docs/assets/screenshots/window-menu.png" width="360" alt="A window's menu, with the Effects menu open">
  <img src="docs/assets/screenshots/settings-appearance.png" width="440" alt="Settings, Appearance page, with sliders for opacity">
</p>

## Use apps through a small view

Press **Ctrl+Alt+P** on a window for a small live view of it that stays on top. Turn on **Interactive** in the view's
menu *(experimental)* and you can use the app right inside the view: click, type, scroll, select text and open menus.
The real window stays hidden out of the way, so a video, chat or document stays at hand in a corner while you work.

**Enhanced hover** keeps hover effects working in browsers and apps like VS Code and Discord. For an app that doesn't
work well through a view, such as a game, a click on its view can bring the real window back instead, and it hides
again when you switch away. [What works where](docs/FEATURE_COMPATIBILITY.md) lists the details.

## Features

**Windows**
- Pin on top, with an optional colored border
- See-through, with a default level, Ctrl+Alt+wheel under the pointer, and solid again while the mouse is over it
- Click-through
- Borderless, fill the display, dark title bar
- Remember a program's setup for its windows from then on

**Picture-in-Picture**
- Live views of a window, a whole display or part of the screen
- Crop, zoom, resize, presets and saved sources
- Keep the original hidden and the view updating
- Use apps through a view, with Enhanced hover and middle-click scrolling *(experimental)*
- Per-view frame limit, opacity, position lock and click-through
- Minimize views to the taskbar, hover preview and reading lens
- Views that move away from text and pictures *(experimental)*

**Anchors**
- Floating buttons that bring a window back with a click
- Drop files on them, rest the pointer for a live preview
- Added and removed for you as windows get covered
- Take their windows off the taskbar, or hide them all for now

**Arrange**
- Nine places on the screen, the next display, an exact position and size, the previous position
- Move and resize by holding Alt or Win and dragging, with edge snapping
- Roll up to the title bar *(experimental)*, send to the back

**Effects** *(experimental)*
- Dark mode, and one that keeps pictures and video as they are
- Glass that fades only the background
- Crop a window, or shrink it to a live tile

**Everyday use**
- A toolbar on title bars, a window menu on Ctrl+right-click, and a full tray menu
- Shortcuts you can change, and a searchable list of every action
- Settings that look like Windows 11's, in light or dark
- Programs to leave alone, or the only ones to work on
- Pauses during games and full-screen apps
- Runs as administrator when you want it to, for apps such as Task Manager
- Put everything back with Ctrl+Alt+R; windows come back even if WinScreen stops unexpectedly
- Automatic updates, and settings you can move to another PC
- English, 简体中文 and 繁體中文

## Install

Download **WinScreen-…-x64-Setup.exe** from [Releases](https://github.com/DarkLian/WinScreen/releases) and run it. It works on
Windows 10 (version 2004 or later) and Windows 11, on 64-bit Intel or AMD PCs (x64). See
[what works where](docs/FEATURE_COMPATIBILITY.md) for other systems and for what each feature can and can't do.

You don't need anything else installed, and it runs without administrator rights. WinScreen lives in the system tray.

The installer isn't signed yet, so Windows may say it doesn't recognize it. Choose **More info**, then **Run anyway**.
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

## Code signing policy

**WinScreen isn't signed yet.** We've applied to [SignPath Foundation](https://signpath.org/) for free code signing
and are waiting to hear back. Once they accept, releases will be signed, with free code signing provided by
[SignPath.io](https://about.signpath.io/) and the certificate by SignPath Foundation.

- **Who's involved:** [DarkLian](https://github.com/DarkLian) writes and reviews WinScreen's code, with help from AI
  agents, and approves every signed release. Nobody else can change the code or sign a release.
- **Privacy:** WinScreen doesn't send anything about you or your PC anywhere. It only goes online when you turn on
  automatic updates or choose **Check for updates**, and then it just asks GitHub for a newer version.

## License

[MIT](LICENSE)
