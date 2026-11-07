# WinScreen User Guide

[English](USER_GUIDE.md) · [简体中文](USER_GUIDE.zh-CN.md) · [繁體中文](USER_GUIDE.zh-TW.md)

[Back to README](../README.md)

## First use

Install and start WinScreen. Look for its icon in the Windows system tray; expand the hidden icons if needed.
Click the icon to open Settings. Right-click it for every command, including restoring windows and exiting.
Hotkeys act on the active window. The tray menu acts on the target named at its top, which is the most recently
active eligible window.

Rest the pointer on the middle of a window's title bar to show the toolbar. You can disable the toolbar in Settings.
WinScreen leaves the desktop, taskbar, and excluded programs alone. Applications running as administrator may refuse changes.

## Default shortcuts

Activate the intended window before pressing a shortcut. All shortcuts can be changed or cleared in **Settings → Hotkeys**.
Click a hotkey field and press a combination; press Delete to clear it. Combinations need Ctrl, Alt or Win, though
function keys also work on their own. Save applies changes and reports combinations that are taken or used twice.

If another app holds a hotkey when WinScreen starts, WinScreen tells you which one and the others keep working.
A Ctrl+Alt combination that types a character on your keyboard can't be used, because Windows treats AltGr as
Ctrl+Alt. WinScreen skips that hotkey and tells you, so you can choose another.

| Action | Shortcut |
| --- | --- |
| Pin / unpin on top | Ctrl+Alt+T |
| Increase opacity | Ctrl+Alt+Up |
| Decrease opacity | Ctrl+Alt+Down |
| Toggle click-through | Ctrl+Alt+C |
| Open / close Picture-in-Picture | Ctrl+Alt+P |
| Restore all changed windows | Ctrl+Alt+R |
| Restore only this window (also works for excluded targets) | Ctrl+Alt+U |
| Toggle dark mode | Ctrl+Alt+D |
| Toggle Glass | Ctrl+Alt+G |
| Add / remove Anchor | Ctrl+Alt+A |
| Toggle default transparency | Ctrl+Alt+O |
| Shrink to a live tile | Ctrl+Alt+W |
| Crop / remove crop | Ctrl+Alt+X |
| Remember / forget this program's setup | Ctrl+Alt+Shift+S |

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
| Right-click | Open the standard context menu |
| Middle-click | Request that the window close, and immediately remove the Anchor |
| Shift+right-click / Shift+F10 | Open the same menu: Show, Minimize, Close, or Remove Anchor only |
| Menu → Remove Anchor only | Dismiss the shortcut while leaving its window open |
| Drag a file over it | Bring its window forward so the file can be dropped there |

Middle-click does not force an application to quit. The application may show an unsaved-work prompt, cancel closing,
or hide to its own tray. The Anchor still disappears; add it again with Ctrl+Alt+A if wanted.
Anchors are also removed when their target window is destroyed, when restoring all windows, or when WinScreen exits.

### Picture-in-Picture controls

Press **Ctrl+Alt+P** to open a live view. The original window keeps its real size.

To show something else, right-click the tray icon and choose **Picture-in-Picture views → Choose what to show**.
You can search for a window, pick a whole display, or use **Select a region** and drag out part of a display.
Select a display first to choose which one; otherwise the region comes from the display under the pointer.
You can give this command a hotkey in Settings. Views of a display or region use Advanced capture, whatever
the rendering setting says, and show the pointer. Windows shows its capture border while they are open.
Close them with their ✕, their menu, or the tray's **Picture-in-Picture views** list.

In Settings → **Picture-in-Picture**, choose **Auto**, **Compatibility (DWM)** or **Advanced (WGC)**.
Existing settings keep Compatibility until you change it. New installations use Auto, which shows DWM first and
switches only after advanced capture has presented a frame. Advanced capture keeps Windows' capture indicator;
availability depends on Windows, the source and the graphics device. A failure returns to DWM with a tray result.

The advanced backend supports a frame limit (Match Monitor or 15–480 FPS), area downscaling, nearest-texel and
linear filtering. These controls do not change DWM's compositor-managed cadence or filtering. A cap is an upper
limit, not a promise that the source produces that many frames. PiP opacity affects the picture independently of
the original window; it fades toward the view's black background, keeping the close button usable. Output is SDR;
HDR and protected content can have limitations. Minimized or idle sources retain their last frame.

Right-click a view for **PiP diagnostics** and, after fallback, **Retry advanced backend** (10-second cooldown).
Diagnostics distinguish the actual backend, requested and observed FPS, drops and capture-to-Present return time;
this is not physical display latency. Enable hardware details in Settings to include the adapter and feature level
when copying diagnostics. DWM has no application-owned capture timing. Software capture, when it passes a real
start-to-present probe, is limited to 30 FPS. Shrink continues to use DWM.

| Input on PiP | Result |
| --- | --- |
| Click | Go to the original window; no synthetic click is sent by default |
| Drag | Move the view |
| Mouse wheel | Resize the view |
| Ctrl+mouse wheel | Zoom into the picture, up to 8×; Ctrl+double-click resets it |
| Middle-button drag | Move around a zoomed picture |
| Ctrl+drag | Select a crop |
| Ctrl+double-click | Remove the crop |
| Right-click | Hide/show the original, go to it, remove crop, or close PiP |
| The ✕ button shown while the pointer is on the view | Close PiP |

Right-click a view for **View options**. Each view can have its own frame limit, scaling, and opacity, can lock its
position, and can become click-through. These last only as long as the view unless you save them. **Size** offers
Small, Medium, Large, and Fit to source. **Presets** saves the crop, size, position, and options under a name so you
can apply them to any view later. A click-through view can't be clicked, so turn it off from the tray's
**Picture-in-Picture views** list; its opacity is then real see-through.

By default, clicking PiP shows/activates the original so you can interact with it normally. This does not move the
system pointer or inject a click. Only one PiP or Shrink tile is kept per source; opening one replaces the other.
Pressing Ctrl+Alt+P again on the same window closes its view. Dragging, resizing and cropping a view leave the keyboard
with the app you're using. Esc cancels a crop selection once Ctrl is released.

To opt into click forwarding, enable both **Experimental effects** and **Forward PiP clicks** in General settings.
The original is then temporarily positioned beneath the pointer. Before forwarding, WinScreen checks its lifetime
identity, foreground activation, visibility, click-through style, and the actual window under the point. A refused or
covered target cancels the click and falls back to **Go to original**. Failed movement is rolled back, with failed
cleanup retained for recovery. A maximized source stays maximized. This remains experimental because focus and real
input cannot be made atomic with other applications; leave it off for sensitive or unsaved content.

PiP usually keeps updating while its original is covered or hidden and closes when the original is destroyed.
Browsers and apps built on them, such as Chrome, Edge and Discord, stop drawing while fully covered, so their view freezes.
Choose **Keep updating (hide original)** from the view's menu to keep them live: a hidden original stays above other windows,
invisible and click-through, so it keeps drawing. Minimized apps show their last frame. The view then says **Source minimized**; click that note to keep it updating, or restore the app yourself.
WinScreen never restores a window on its own while a view is open. Opening a view on a minimized window does restore it, and says so.
A hidden original still appears in Alt-Tab and the taskbar; use **Keep updating (hide original)** again to show it.

**Move away from text and images** looks for an emptier screen area about once per second. It stays still while the pointer
is over the view and when no better spot exists. It estimates content by contrast rather than understanding text.
While enabled, PiP is excluded from screenshots and screen sharing so it can inspect the area underneath itself.
The General setting enables this for newly opened views. While experimental effects are on, each view also has its
own menu toggle.

Selecting text directly by dragging inside PiP is not implemented. Clicking PiP first lets you interact with the original.

## Transparency and click-through

Ctrl+Alt+Up/Down adjusts opacity in steps of about 10%, never below 20%. **Ctrl+Alt+O** toggles between your configured
default opacity and a solid window. Configure the default in **Settings → General** (20–95% solid).

**Ctrl+Alt+C** makes mouse clicks pass through to the window underneath. It also applies the default transparency
when the window does not already have WinScreen transparency, and removes it again when you turn click-through off.
A click-through window stays see-through while the pointer is over it, even with solid-on-hover enabled.

### Make a click-through window clickable again

1. For one window, select it with **Alt-Tab**, then press **Ctrl+Alt+C** again.
2. For full recovery, press **Ctrl+Alt+R**, or right-click WinScreen's tray icon and choose **Restore all windows**.
   This undoes all WinScreen window changes, not only click-through.
3. If hotkeys are disabled, reassigned, or paused by a full-screen app, use the tray restore action.

Shortcuts act on the active window. Pressing Ctrl+Alt+C while another window is active changes that other window.
Click-through cannot be saved as a program's automatic setup.

## Dark mode, Glass, Crop, and Shrink

These effects and PiP auto-move are **experimental, disabled by default**. Enable them explicitly in General settings.
Their toolbar buttons and tray commands appear only while they are enabled.
Disabling experimental effects cancels crop selection and removes those effects where cleanup succeeds. Any failure
remains visible and retryable. Glass samples visible screen content and can be affected by overlapping windows.

- **Dark mode** swaps light and dark while preserving colors through an overlay. It changes the appearance on screen;
  a PiP view keeps the original colors.
- **Glass** removes the window's most common color. It works best with a flat background and depends on how the app draws.
- **Crop** dims the window so you can drag over the part to keep; Esc cancels. The rest disappears and clicks there
  reach what is behind. The app is not resized. If you remove its title bar, uncrop before moving it. Apps that draw
  their own shape may reject cropping or redraw their region.
- **Shrink** replaces the window with a small live tile while retaining the window's actual size. Click the tile,
  select the window in Alt-Tab, select it on the taskbar, or press the Shrink hotkey again to restore it.
  Drag the tile to move it, or right-click it for its menu.

## Arrange a window

The toolbar's **Arrange window** button lists everything that moves a window, and each entry can also have a hotkey in
**Settings → Hotkeys**. None has one by default.

- **Nine positions:** the corners, the middle of each edge, and the center of the screen.
- **Move to next display:** keeps the same relative place on the next display.
- **Exact position and size:** enter the visible frame's position and size in screen pixels.
- **Restore prior position:** steps back through recent moves. This is forgotten when WinScreen exits.
- **Send to bottom:** puts the window behind all others. Unpin it first.
- **Window information:** shows the window's handle, process, program file, class, position, and display.

A minimized or maximized window stays that way and gets a new place to come back to. **Restore all windows** doesn't undo
these moves, because you chose them.

## Remember a program's setup

Adjust pinning, opacity, and dark mode, then press **Ctrl+Alt+Shift+S**. Windows belonging to that program receive the setup
the first time they come forward, including after a restart. Later manual changes stay changed.

New setups are matched by executable path, which is displayed in Saved setups. Older name-only entries remain
compatible until you resave them. A failed application retries after five seconds; successful setups are not reapplied.
Saved dark mode is only applied while experimental effects are enabled.

Press the shortcut on a window with no such changes to forget its program's saved setup, or remove it in
**Settings → Saved setups**. Click-through, Glass, Crop, and Shrink are deliberately not saved.

## Settings

| Tab / option | Behavior |
| --- | --- |
| General → Language | System default, English, 简体中文, or 繁體中文; save and restart to apply |
| General → Show confirmations | Controls messages for actions that worked. Problems always appear |
| General → Automatic updates | Opt-in download and install-on-exit behavior described below |
| General → Experimental effects / Forward PiP clicks | Opt-in controls for effects still under validation |
| General → Start when signing in | Starts WinScreen for your Windows user |
| General → Pause new commands during games and full-screen apps | Stops new commands. Existing effects and hotkey assignments stay, and restoring windows still works. Ctrl+Alt+P still closes the latest view |
| General → Still allow opening Picture-in-Picture in full-screen apps | Lets Ctrl+Alt+P open views during borderless full-screen apps. Exclusive full-screen stays refused |
| General → Pause pinning when maximized | Temporarily suspends pinning for maximized windows |
| General → Solid on hover | Makes transparent windows solid while the pointer is over them |
| General → Red border | Highlights pinned windows |
| Programs → Exclusions | Programs WinScreen should leave alone |
| Programs → Inclusions | Restricts control to these programs; leave empty for all eligible programs |
| Saved setups | Lists and removes remembered program setups |

Program lists accept names such as `ExampleProgram` or `ExampleProgram.exe`, or you can choose a running program from
the list. Press Enter or Add, or just Save, to include what you typed.

### Automatic updates

Enable **Download updates automatically; install when WinScreen exits**, then save. WinScreen checks after about
30 seconds and every six hours. Only installed copies update. New stable versions are downloaded and verified in
the background; installation is scheduled when you exit normally, after window restoration. WinScreen does not restart itself.

Turning this option off cancels in-flight work and prevents installation of a downloaded update. The current app keeps
running if the network is unavailable or verification fails. Update notices obey the notification toggle.
Use **Check for updates** in the tray menu to check right away. Local development/portable runs do not update, and
Settings explains why when the option isn't available.
The repository must have correctly signed releases before updates are available; see [security details](../SECURITY.md).

For a manual upgrade, exit WinScreen and run the newer installer. No uninstall is normally required. Settings are
kept in `%APPDATA%\WinScreen`, separately from the installed program.

## Exit, restore, and troubleshooting

Failed restoration steps keep their recovery records for retry. If cleanup fails on exit, WinScreen stays running
and does not schedule an update. Open the tray's **Last result** for details, then retry the per-window or all-window
restore action when the target responds. Damaged or unverifiable legacy recovery data is preserved in `.invalid-*`
backups, and WinScreen tells you where. If the recovery file can't be read at all, for example because another program
has it open, new changes pause until **Restore all windows** can read it. Exiting still works.

- **Restore all windows** removes pinning, transparency, click-through, hiding, crops, dark overlays, and Anchors.
- Exiting WinScreen restores changed windows. After a forced termination or crash, the next start uses its recovery journal.
- If a shortcut does nothing, check for conflicts in Settings, exclusions/inclusions, full-screen pause, or an elevated target app.
- If an Anchor disappears but the app remains, it may have cancelled closing or hidden itself. Find the app on the taskbar or in Alt-Tab.
- If a PiP view freezes, choose **Keep updating (hide original)** from its menu. Apps can stop drawing while covered or minimized.
- If Settings looks unchanged after choosing a language, save, exit WinScreen, and reopen it.
- Display scaling above 100%, multiple monitors, Windows 10, and real installed-version upgrades still need broader validation.

## Known limitations

These are limits of the current version, listed so you can decide before you rely on a feature.

- **Windows:** WinScreen supports 64-bit Windows 11. Windows 10 is not supported or tested.
- **Testing so far:** one 1920×1080 display at 100% scaling. Multiple monitors, mixed scaling, and other Windows editions are unverified.
- **Administrator apps:** WinScreen can't change windows running as administrator.
- **Minimized apps:** a minimized app stops drawing, so its view shows the last picture. Windows gives WinScreen no new frames.
- **Browsers and apps built on them:** these can stop drawing when another window fully covers them. **Keep updating (hide original)** keeps them updating.
- **Keep updating (hide original):** the original keeps running, stays in Alt-Tab and the taskbar, and appears above other windows when shown again.
- **Exclusive full-screen games:** views can't reliably appear over them, and WinScreen will not hook into games to do so. Use borderless or windowed mode.
- **Picture quality:** output is SDR. Protected video may appear black.
- **PiP opacity:** a view fades toward black. It only becomes see-through while click-through.
- **Audio:** PiP shows the picture only.
- **Text in PiP:** you can't select text inside a view.
- **Click forwarding:** experimental, because focus and real input can't be made atomic with other apps. Leave it off for sensitive or unsaved work.
- **Dark, Glass, and Crop:** results depend on how each app draws.
- **Compatibility mode:** WinScreen can only tell that a source is minimized, not that it has otherwise stopped updating. In Advanced mode, **No new picture** can also mean the source is simply idle.
- **Full-screen apps:** commands stay paused unless you opt in, but views can always be closed.
- **Display and region views:** they need Advanced capture and have no Compatibility fallback, and Windows shows its capture border. If capture stops, the view closes.
- **Screen sharing:** display and region views don't appear in your own screenshots or screen sharing, because otherwise a view would capture itself.
- **Regions:** a region stays on one display.
- **Presets:** a saved crop is in pixels, so it may not fit a differently sized window.
- **Click-through views:** they can't be clicked, so use the tray menu or **Restore all windows** to reach them.
- **Arranging windows:** some apps enforce their own minimum size or position, and multiple displays and mixed scaling are unverified.
