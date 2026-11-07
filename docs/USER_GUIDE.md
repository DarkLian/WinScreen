# WinScreen User Guide

[English](USER_GUIDE.md) · [简体中文](USER_GUIDE.zh-CN.md) · [繁體中文](USER_GUIDE.zh-TW.md)

[Back to README](../README.md)

## First use

Install and start WinScreen. Look for its icon in the Windows system tray; expand the hidden icons if needed.
Right-click the icon to open Settings, restore windows, or exit. Commands normally act on the active window;
the tray commands marked “last window” act on the most recently active eligible window.

Rest the pointer on the middle of a window's title bar to show the toolbar. You can disable the toolbar in Settings.
WinScreen leaves the desktop, taskbar, and excluded programs alone. Applications running as administrator may refuse changes.

## Default shortcuts

Activate the intended window before pressing a shortcut. All shortcuts can be changed or cleared in **Settings → Hotkeys**.
Click a hotkey field and press a combination; press Delete to clear it. Save applies changes and reports combinations another app already uses.

| Action | Shortcut |
| --- | --- |
| Pin / unpin on top | Ctrl+Alt+T |
| Increase opacity | Ctrl+Alt+Up |
| Decrease opacity | Ctrl+Alt+Down |
| Toggle click-through | Ctrl+Alt+C |
| Open Picture-in-Picture | Ctrl+Alt+P |
| Restore all changed windows | Ctrl+Alt+R |
| Toggle dark mode | Ctrl+Alt+D |
| Toggle Glass | Ctrl+Alt+G |
| Add / remove Anchor | Ctrl+Alt+A |
| Toggle default transparency | Ctrl+Alt+O |
| Shrink to a live tile | Ctrl+Alt+W |
| Crop / remove crop | Ctrl+Alt+X |
| Remember / forget this program's setup | Ctrl+Alt+S |

## Pin, Picture-in-Picture, or Anchor?

| Feature | What it does | When to use it |
| --- | --- | --- |
| Pin on top | Keeps the actual window above other windows | Work in a window while keeping it visible |
| Picture-in-Picture (PiP) | Creates a separate small live view of the original | Watch an app while saving screen space |
| Anchor | Adds a floating shortcut button, without a preview | Bring a buried window forward quickly |

### Anchor controls

Press **Ctrl+Alt+A** on a window to add or remove its Anchor. Drag the Anchor to reposition it.

| Input on the Anchor | Result |
| --- | --- |
| Left-click | Restore a minimized window and bring it forward |
| Right-click | Minimize its window |
| Middle-click | Request that the window close, and immediately remove the Anchor |
| Shift+right-click | Open a menu: Show, Minimize, Close, or Remove Anchor only |
| Menu → Remove Anchor only | Dismiss the shortcut while leaving its window open |
| Drag a file over it | Bring its window forward so the file can be dropped there |

Middle-click does not force an application to quit. The application may show an unsaved-work prompt, cancel closing,
or hide to its own tray. The Anchor still disappears; add it again with Ctrl+Alt+A if wanted.
Anchors are also removed when their target window is destroyed, when restoring all windows, or when WinScreen exits.

### Picture-in-Picture controls

Press **Ctrl+Alt+P** to open a live view. The original window keeps its real size.

| Input on PiP | Result |
| --- | --- |
| Click | Bring the original forward at the clicked location and interact with it |
| Drag | Move the view |
| Mouse wheel | Resize the view |
| Ctrl+drag | Select a crop |
| Ctrl+double-click | Remove the crop |
| Right-click | Hide/show the original, go to it, remove crop, enable automatic movement, or close PiP |

When you click PiP, WinScreen brings the actual window under the cursor at full size, with the clicked spot beneath it,
and passes the click on. You then use the original app with ordinary mouse and keyboard input. The window goes back
when the pointer leaves it. A maximized original stays maximized; move the pointer back over PiP to return to its view.
If the maximized original's clicked point is covered by PiP, the initial click may not be forwarded to avoid clicking the view again.

PiP usually keeps updating while its original is covered or hidden and closes when the original is destroyed.
Some apps stop drawing when covered, so their previews may freeze. A hidden original still appears in Alt-Tab and the taskbar;
use PiP's **Hide original window** menu item again to show it.

**Move away from text and images** looks for an emptier screen area about once per second. It stays still while the pointer
is over the view and when no better spot exists. It estimates content by contrast rather than understanding text.
While enabled, PiP is excluded from screenshots and screen sharing so it can inspect the area underneath itself.
The General setting enables this for newly opened views; each view also has its own menu toggle.

Selecting text directly by dragging inside PiP is not implemented. Clicking PiP first lets you interact with the original.

## Transparency and click-through

Ctrl+Alt+Up/Down adjusts opacity in steps of about 10%, never below 20%. **Ctrl+Alt+O** toggles between your configured
default opacity and a solid window. Configure the default in **Settings → General** (20–95% solid).

**Ctrl+Alt+C** makes mouse clicks pass through to the window underneath. It also applies the default transparency
when the window does not already have WinScreen transparency.

### Make a click-through window clickable again

1. For one window, select it with **Alt-Tab**, then press **Ctrl+Alt+C** again. Its transparency may remain.
2. For full recovery, press **Ctrl+Alt+R**, or right-click WinScreen's tray icon and choose **Restore all windows**.
   This undoes all WinScreen window changes, not only click-through.
3. If hotkeys are disabled, reassigned, or paused by a full-screen app, use the tray restore action.

Shortcuts act on the active window. Pressing Ctrl+Alt+C while another window is active changes that other window.
Click-through cannot be saved as a program's automatic setup.

## Dark mode, Glass, Crop, and Shrink

- **Dark mode** swaps light and dark while preserving colors through an overlay. It changes the appearance on screen;
  a PiP view keeps the original colors.
- **Glass** removes the window's most common color. It works best with a flat background and depends on how the app draws.
- **Crop** dims the window so you can drag over the part to keep; Esc cancels. The rest disappears and clicks there
  reach what is behind. The app is not resized. If you remove its title bar, uncrop before moving it. Apps that draw
  their own shape may reject cropping or redraw their region.
- **Shrink** replaces the window with a small live tile while retaining the window's actual size. Click the tile,
  select the window in Alt-Tab, or select it on the taskbar to restore it. Drag the tile to move it.

## Remember a program's setup

Adjust pinning, opacity, and dark mode, then press **Ctrl+Alt+S**. Windows belonging to that program receive the setup
the first time they come forward, including after a restart. Later manual changes stay changed.

Press the shortcut on a window with no such changes to forget its program's saved setup, or remove it in
**Settings → Saved setups**. Click-through, Glass, Crop, and Shrink are deliberately not saved.

## Settings

| Tab / option | Behavior |
| --- | --- |
| General → Language | System default, English, 简体中文, or 繁體中文; save and restart to apply |
| General → Show notifications | Controls startup notices, action confirmations, and operation-error balloons immediately after saving |
| General → Automatic updates | Opt-in download and install-on-exit behavior described below |
| General → Start when signing in | Starts WinScreen for your Windows user |
| General → Pause during games / full-screen apps | Pauses ordinary commands; tray Restore all remains available |
| General → Pause pinning when maximized | Temporarily suspends pinning for maximized windows |
| General → Solid on hover | Makes transparent windows solid while the pointer is over them |
| General → Red border | Highlights pinned windows |
| Programs → Exclusions | Programs WinScreen should leave alone |
| Programs → Inclusions | Restricts control to these programs; leave empty for all eligible programs |
| Saved setups | Lists and removes remembered program setups |

Program lists accept names such as `ExampleProgram` or `ExampleProgram.exe`. Turning notifications off does not hide
hotkey conflicts inside Settings or unexpected-failure dialogs.

### Automatic updates

Enable **Download updates automatically; install when WinScreen exits**, then save. WinScreen checks after about
30 seconds and every six hours. Only installed copies update. New stable versions are downloaded and verified in
the background; installation is scheduled when you exit normally, after window restoration. WinScreen does not restart itself.

Turning this option off cancels in-flight work and prevents installation of a downloaded update. The current app keeps
running if the network is unavailable or verification fails. Update notices obey the notification toggle.
Local development/portable runs do not update. A build without a configured source shows a notice in Settings.
The repository must have correctly signed releases before updates are available; see [security details](../SECURITY.md).

For a manual upgrade, exit WinScreen and run the newer installer. No uninstall is normally required. Settings are
kept in `%APPDATA%\WinScreen`, separately from the installed program.

## Exit, restore, and troubleshooting

- **Restore all windows** removes pinning, transparency, click-through, hiding, crops, dark overlays, and Anchors.
- Exiting WinScreen restores changed windows. After a forced termination or crash, the next start uses its recovery journal.
- If a shortcut does nothing, check for conflicts in Settings, exclusions/inclusions, full-screen pause, or an elevated target app.
- If an Anchor disappears but the app remains, it may have cancelled closing or hidden itself. Find the app on the taskbar or in Alt-Tab.
- If a PiP view freezes, show its original window. The app may suspend rendering when covered.
- If Settings looks unchanged after choosing a language, save, exit WinScreen, and reopen it.
- Display scaling above 100%, multiple monitors, Windows 10, and real installed-version upgrades still need broader validation.

For build instructions and the full validation limits, see the [Development Guide](DEVELOPMENT.md).
