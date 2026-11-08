# WinScreen User Guide

[English](USER_GUIDE.md) · [简体中文](USER_GUIDE.zh-CN.md) · [繁體中文](USER_GUIDE.zh-TW.md) · [Back to README](../README.md)

## Getting started

After you install WinScreen, its icon sits in the system tray (look under the small arrow if you don't see it).

- **Click** the icon to open Settings.
- **Right-click** it for every command, including putting windows back and exiting.
- **Hotkeys** work on the window you're using. The tray menu works on the window named at its top.
- **Rest the pointer** on the middle of a window's title bar to show the toolbar.

Some features are marked *experimental*. Turn them on in **Settings → General → Experimental**.

## Shortcuts

You can change or clear any of these in **Settings → Actions and shortcuts**. Select an action, click its box and press the keys, or press Delete to clear it.

| Do this | Shortcut |
| --- | --- |
| Pin or unpin on top | Ctrl+Alt+T |
| More opaque / more see-through | Ctrl+Alt+Up / Down |
| Switch default see-through on or off | Ctrl+Alt+O |
| Click-through on or off | Ctrl+Alt+C |
| Open or close a live view | Ctrl+Alt+P |
| Add or remove an Anchor | Ctrl+Alt+A |
| Dark mode | Ctrl+Alt+D |
| Glass | Ctrl+Alt+G |
| Shrink to a live tile | Ctrl+Alt+W |
| Crop or uncrop | Ctrl+Alt+X |
| Remember or forget this program's setup | Ctrl+Alt+Shift+S |
| Put this window back | Ctrl+Alt+U |
| Put every window back | Ctrl+Alt+R |

Other commands, like moving a window to a corner, have no shortcut until you give them one.

## Pin and see-through

- **Ctrl+Alt+T** keeps a window above the others.
- **Ctrl+Alt+Up/Down** changes how solid it is. It never goes below 20%.
- **Ctrl+Alt+O** switches between solid and your default see-through level, set in Settings → Appearance.
- **Ctrl+Alt+C** lets clicks pass through the window to whatever is behind it. Press it again to turn it off.

If you can't reach a click-through window, press **Ctrl+Alt+R** to put every window back.

In **Settings → Appearance** you can change the opacity step, give pinned windows a colored border, and turn on
**Ctrl+Alt+wheel**, which changes the opacity of the window under the pointer.

## Picture-in-Picture views

Press **Ctrl+Alt+P** to open a small live view of the window you're using. Press it again to close it.
To show a whole display or part of the screen, right-click the tray icon and choose **Picture-in-Picture...**.
You can save a display or area there and open it again later from **Picture-in-Picture views → Saved sources**.

| On a view | What happens |
| --- | --- |
| Click | Go to the original window |
| Drag | Move the view |
| Wheel | Resize the view |
| Ctrl+wheel | Zoom in; Ctrl+double-click to zoom out |
| Ctrl+drag | Crop to the part you want; Ctrl+double-click to remove the crop |
| Right-click | Open the view's menu |

Hover over a view to see its title bar, with buttons to minimize and close it.

**Useful options in the view's menu**

- **Keep updating (hide original):** some apps, such as browsers, stop drawing when they're covered. This hides the original
  so the view stays live.
- **View options:** frame limit, opacity, size, position lock and click-through, just for this view.
- **Presets:** save the crop, size and place under a name and use it again later.
- **Use this view directly** *(experimental)*: click, select text and scroll right inside the view.
  Presses on the original's title bar move the view instead, and keys go back to your previous window when you leave.

**In Settings → Picture-in-Picture** you can choose a hover preview or reading lens, and keep minimized views on the taskbar.

## Anchors

An Anchor is a small floating button for a window. Press **Ctrl+Alt+A** to add or remove one.

- **Click** brings the window back.
- **Right-click** opens a menu to show, minimize or close it.
- **Middle-click** asks the window to close.
- **Drag a file over it** to bring the window forward and drop the file there.

In **Settings → Anchors**, WinScreen can add Anchors by itself for windows that are completely covered, and take them away
again when you can see the window. You can also choose where Anchors appear, how many, their size and style, and whether
they move off text and pictures. A small notch on an Anchor means its window has a view.

## Arrange windows

The toolbar's **Arrange window** button has everything that moves a window:

- Nine places on the screen: corners, edges and center.
- Move to the next display.
- Type an exact position and size.
- Go back to the previous position.
- Send to the back, behind every other window.
- Roll up to the title bar *(experimental)*.

Turn on **Settings → Mouse gestures → Move and resize** to hold Alt (or Win) and drag anywhere in a window: the left button
moves it, the right button resizes it. It works on maximized windows too, and Esc puts the window back. Drag a window to the
left or right edge of the screen to fill that half, to a corner for a quarter, or to the top to maximize it. Turn on **Window menu** there for a menu of commands on **Ctrl+right-click**.

The tray and window menus also have **Borderless**, **Fill display**, **Dark title bar**, **Copy** and **Open file location**.

## Effects *(experimental)*

- **Dark mode** swaps light and dark. Windows that are already dark are left alone.
- **Glass** makes a window's background see-through.
- **Crop** keeps just the part of a window you drag over. Clicks on the rest reach what's behind.
- **Shrink** turns a window into a small live tile. Click the tile to bring the window back.

In **Settings → Appearance** you can make Glass fade only the background while keeping text and pictures, and
make Dark mode leave pictures and video as they are. You can also choose how solid the background, text and pictures stay.
Windows shows a colored border around a window while these two options are working.

## Remember a program's setup

Set a window up the way you like it: pinned, see-through, dark, with a view open if you want one. Then press
**Ctrl+Alt+Shift+S**. From then on, that program's windows start the same way, even after a restart.
Press it again on a plain window to forget the setup, or remove it in **Settings → Saved setups**.

## Settings at a glance

| Page | What you'll find |
| --- | --- |
| General | Language, notifications, starting with Windows, updates, experimental effects, and moving your settings to another PC |
| Actions and shortcuts | Everything WinScreen can do, with its shortcut. Select an action to change its shortcut |
| Programs | Programs WinScreen should leave alone, or the only ones it should work on |
| Saved setups | Program setups you've remembered |
| Picture-in-Picture | How views look and behave |
| Mouse gestures | Move and resize with a key, and the mouse menu |
| Appearance | Opacity, pin border and effects |
| Toolbar | Where the toolbar appears, how quickly, and which buttons it shows |
| Anchors | Automatic Anchors and how Anchors look |

## Putting things back

- **Ctrl+Alt+U** puts the window you're using back to normal.
- **Ctrl+Alt+R**, or **Restore all windows** in the tray menu, puts every window back.
- Closing WinScreen puts every window back too.
- If WinScreen stops unexpectedly, hidden windows come back within a couple of seconds, and the rest when it starts again.

If something didn't work, the tray menu's **Last result** shows what happened.

## Updates

Turn on automatic updates in **Settings → General**. WinScreen downloads new versions in the background and installs them
when you close it. You can also choose **Check for updates** from the tray menu at any time. To update by hand, close WinScreen and run
the newer installer. Your settings stay as they are.

## Good to know

- WinScreen runs on Windows 10 (version 2004 or later) and Windows 11, on x64 and ARM PCs. The ARM version needs Windows 11.
- To change apps running as administrator, including moving them with Alt-drag, start WinScreen as administrator
  too (right-click it, then **Run as administrator**). Starting with Windows starts it without those rights.
- Commands pause while a game or full-screen app is in front. Views can't appear over games in exclusive full screen.
- A minimized app stops drawing, so its view shows the last picture.
- Protected video may show as black in a view.
- In smooth mode Windows draws a yellow border around the window being shown. On Windows 11 WinScreen asks to leave it
  off; you can change that in **Settings → Picture-in-Picture**. Windows 10 always shows it, so choose standard mode there if
  it bothers you.
- Views of a display or region, and Anchors that move off content, don't show up in your own screenshots or screen sharing.
- Results of Dark, Glass and Crop depend on how each app draws itself.
