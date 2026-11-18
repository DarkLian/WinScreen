<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="WinScreen icon">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>Stable.</strong> Features marked <em>experimental</em> may still change. Your feedback helps most right now.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-stable-brightgreen" alt="Status: stable">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/arch-x64-4c8bf5" alt="x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="MIT license"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (Brasil)</a> · <a href="README.ru.md">Русский</a> · <a href="README.uk.md">Українська</a>
</p>

## See it in action

<p align="center">
  <a href="https://www.youtube.com/watch?v=FncLMB3cYDQ"><img src="docs/assets/demos/video-thumbnail.png" width="640" alt="Watch the 5-minute demo on YouTube"></a>
</p>

<p align="center"><a href="https://www.youtube.com/watch?v=FncLMB3cYDQ"><strong>Watch the 5-minute demo on YouTube</strong></a></p>

Two views over a code editor: one plays a video, the other you use right inside the view. They stay with you on another virtual desktop.

<p align="center">
  <img src="docs/assets/demos/views.gif" width="880" alt="Two views over a code editor: one plays a video, the other you use right inside the view. They stay with you on another virtual desktop.">
</p>

The toolbar on a window's title bar: the window's commands, one click away.

<p align="center">
  <img src="docs/assets/demos/toolbar.gif" width="880" alt="The toolbar on a window's title bar: the window's commands, one click away.">
</p>

Anchors bring windows back with a click, from any desktop. Drag a file onto one to drop it into its window.

<p align="center">
  <img src="docs/assets/demos/anchors.gif" width="880" alt="Anchors bring windows back with a click, from any desktop. Drag a file onto one to drop it into its window.">
</p>

Crop a view to the part you want and zoom in. A view of a region of the screen works the same way.

<p align="center">
  <img src="docs/assets/demos/crop-zoom.gif" width="880" alt="Crop a view to the part you want and zoom in. A view of a region of the screen works the same way.">
</p>

Glass fades only the background. Set a window up once, press Ctrl+Alt+Shift+S, and it opens that way next time.

<p align="center">
  <img src="docs/assets/demos/glass-setup.gif" width="880" alt="Glass fades only the background. Set a window up once, press Ctrl+Alt+Shift+S, and it opens that way next time.">
</p>

Glass with a video behind it: any app's window can have its own live background.

<p align="center">
  <img src="docs/assets/demos/backgrounds.gif" width="880" alt="Glass with a video behind it: any app's window can have its own live background.">
</p>

## Use apps through a small view

Press **Ctrl+Alt+P** on a window for a small live view of it that stays on top. Turn on **Interactive** in the view's
menu and you can use the app right inside the view: click, type, scroll, select text and open menus. It's ready for
Chrome, Edge, Brave, Vivaldi, Firefox, Electron apps and File Explorer; for other apps it's *(experimental)*.
The real window stays hidden out of the way, so a video, chat or document stays at hand in a corner while you work.

**Enhanced hover** keeps hover effects working in browsers and apps like VS Code and Discord. For an app that doesn't
work well through a view, such as a game, a click on its view can bring the real window back instead, and it hides
again when you switch away. [What works where](docs/FEATURE_COMPATIBILITY.md) lists the details.

## A picture or video behind any app

Turn on Glass for a window (**Ctrl+Alt+G**) and it can show a picture, animated GIF or video behind its text. Choose
one in **Settings → Backgrounds** for every app, or give an app its own. A video can play its sound, and it pauses
while its window is minimized or on another desktop.

## Features

**Windows**
- Pin on top, with an optional colored border
- See-through, with a default level, Ctrl+Alt+wheel under the pointer, and solid again while the mouse is over it
- Click-through
- Borderless, fill the display, dark title bar
- Remember a program's setup for its windows, matched by title or class, once or every time

**Picture-in-Picture**
- Live views of a window, a whole display or part of the screen
- Crop, zoom, resize, preset sizes, presets and saved sources
- Crop to a part of a window you click, and keep it when the window is resized
- Keep the original hidden and the view updating
- Mute the original's sound from the view
- Use apps through a view: Chrome, Edge, Brave, Vivaldi, Firefox, Electron apps and File Explorer, and other apps, Enhanced hover and middle-click scrolling *(experimental)*
- Per-view frame limit, opacity, position lock and click-through
- Minimize views to the taskbar, hover preview and reading lens
- Views that move away from text and pictures *(experimental)*

**Anchors**
- Floating buttons that bring a window back with a click
- Drop files on them, rest the pointer for a live preview
- Added and removed for you as windows get covered
- Minimize a window to its Anchor
- Take their windows off the taskbar, or hide them all for now
- Show on every virtual desktop, or only on their window's

**Arrange**
- Nine places on the screen, the next display, an exact position and size, the previous position
- Move and resize by holding Alt or Win and dragging, with edge snapping
- Roll up to the title bar *(experimental)*, send to the back

**Effects** *(experimental)*
- Dark mode that keeps pictures and video as they are, or a simple filter
- Glass that fades only the background
- Crop a window, or shrink it to a live tile

**Backgrounds** *(experimental)*
- A picture, animated GIF or video behind a window's text, through Glass
- One background for every app, and apps with their own or with none
- Fill, fit, stretch, tile or center
- A large preview: click it to choose a file, or drop one onto it
- A video's sound, with its own volume, from the window you used last
- Videos pause while their window is minimized or on another desktop

**Everyday use**
- A toolbar on title bars, a window menu on Ctrl+right-click, and a full tray menu
- Shortcuts you can change, and a searchable list of every action
- A command line for scripts
- Settings and menus that work with the keyboard alone
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

The installer isn't signed, so Windows may say it doesn't recognize it. Choose **More info**, then **Run anyway**.
To remove it, go to **Settings → Apps → Installed apps** in Windows.

With [Scoop](https://scoop.sh), it installs in two lines (the portable version, updated by Scoop):

```
scoop bucket add winscreen https://github.com/DarkLian/WinScreen
scoop install winscreen
```

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

## Q&A

### Is it AI slop?

WinScreen started out of passion, to solve problems I actually had, and it's built with a lot of help from AI agents.
I'd rather say that up front.

What I can do is check the work: each release is tested on Windows 10 and Windows 11, features that aren't reliable
yet are marked *experimental*, and reported bugs get fixed rather than left open. What I can't do alone is cover every
Windows version, PC and app. That's where your reports help most: if something doesn't work for you, please
[open an issue](https://github.com/DarkLian/WinScreen/issues).

### Why not just use existing mature tools?

[PowerToys](https://github.com/microsoft/powertoys) is actively maintained, yet it emphasizes the breadth of features
and lacks targeted enhancements. WinScreen exists as a complement that specializes in certain Windows utilities. It is
not here to replicate PowerToys, which would be pointless.

[WindowTop](https://windowtop.info/) is what inspired this project. However, it is closed source and paid. WinScreen
aims to offer a free, open source option for people (and myself) who want features that are locked behind paywalls.
On top of that, without the constraints of a business, WinScreen gets to update rapidly and without burden, so I aim to
respond and iterate quickly, fixing bugs and adding features people actually need (with thorough tests, I hope).

Many other projects I have found are either discontinued, paid, or real AI slop that doesn't get updated and leaves
issues I found on Day 1 open, and their GitHub is a mess.
**There simply doesn't exist an option that offers free, open source, targeted enhancements with continuous support at
this time.**

### Will the project be continuously maintained?

I will try my best to. Yet as a passion-driven, non-profit project, community support is a huge boost for me. I can't
guarantee it will be maintained constantly, but even then it is MIT licensed, so anyone can fork it and keep it alive.
That's the spirit of open source!

### Is it safe?

The project is fully open source. Anyone can inspect the code (or tell AI to analyze it) and build it themselves.
Turn automatic updates off and analyze the code with AI if you want extra security.

Open source doesn't mean absolutely safe, but everything is out in the open for anyone to check, question or point out
problems. I welcome any security concerns you report.

## Updates

In Settings, choose what WinScreen does about new versions: nothing (the default), tell you about them, or install
them for you when you close it. A portable copy can't update itself, so it tells you and opens the new version's page.
To update by hand, close WinScreen and run the newer installer. Your settings stay as they are.

## Tell us what you think

WinScreen is made by DarkLian with the help of AI agents. The best way to help is to tell us how it works for you.
[Open an issue](https://github.com/DarkLian/WinScreen/issues) to share an idea, report a problem, or mention something
small that bugs you. See **[Contributing](CONTRIBUTING.md)** for what helps most.

If WinScreen is useful to you, you can [support it on Ko-fi](https://ko-fi.com/darklian).

## Build it yourself

You need the .NET 10 SDK named in `global.json`. Run `.\build.ps1 -SkipInteractive`, and the installer appears in `dist\release`.

How each feature behaves with every kind of window, and where the code decides it, is in
[Feature compatibility](docs/FEATURE_COMPATIBILITY.md).

## Code signing policy

**WinScreen isn't signed.** Free code signing from [SignPath Foundation](https://signpath.org/) needs a project to be
more widely used first, so we'll apply again later.

- **Who's involved:** [DarkLian](https://github.com/DarkLian) writes and reviews WinScreen's code, with help from AI
  agents, and approves every release. Nobody else can change the code or publish a release.
- **Privacy:** WinScreen doesn't send anything about you or your PC anywhere. It only goes online when you ask to hear
  about new versions or have them installed, or choose **Check for updates**, and then it just asks GitHub which version
  is newest. It keeps a short
  log on your PC to help fix problems, which never leaves your PC unless you send it yourself.

## License

[MIT](LICENSE)
