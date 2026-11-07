# WinScreen User Guide

[English](USER_GUIDE.md) · [简体中文](USER_GUIDE.zh-CN.md) · [繁體中文](USER_GUIDE.zh-TW.md)

[Back to README](../README.md)

## First use

Install and start WinScreen. Look for its icon in the Windows system tray; expand the hidden icons if needed.
Click the icon to open Settings. Right-click it for every command, including restoring windows and exiting.
Hotkeys act on the active window. The tray menu acts on the target named at its top, which is the most recently
active eligible window.

Rest the pointer on the middle of a window's title bar to show the toolbar. **Settings → Toolbar** changes its placement, hover delay, and buttons, and can open it from the Minimize button instead. You can disable the toolbar in General settings.
WinScreen leaves the desktop, taskbar, and excluded programs alone. Applications running as administrator may refuse changes.

## Window menu and appearance

**Settings → Appearance and files** lets you choose opacity steps from 1–25%, and the pin border's colour, opacity,
and thickness. The existing 20% minimum window opacity stays. Optional **Ctrl+Alt+wheel** adjusts the window under
the pointer and pauses during full-screen apps and detected games. It cannot control administrator apps.
Already-dark detection leaves dark windows alone and reports this when you request Dark mode; you can switch detection off.

Use **Export saved settings** to move preferences to another PC. The JSON file includes saved program paths.
**Import settings** loads a file for review; Save checks the hotkeys, keeps a dated backup beside the current settings,
then applies it. Cancel discards the import. Startup on this PC and the first-run state stay local.

Enable **Settings → Mouse gestures → Window menu** to use **Ctrl+right-click** on a window. Ctrl+Shift+right-click
and Ctrl+middle-click are alternatives. Move/resize and the window menu have separate program exclusions; the main
exclusion list applies to both. The menu is off by default, pauses during full-screen apps and detected games, and
leaves administrator apps and WinScreen's own views alone. You can assign **Window menu** a hotkey to open it for
the window under the pointer without enabling the mouse gesture. Esc or an outside click closes it. If Windows
refuses menu activation, Esc and outside clicks still close it, but arrow navigation is unavailable.

The tray and window menus offer **Copy** (title, HWND, process ID, program path, or all window information),
**Open file location**, **Dark title bar**, and **Borderless**. These new commands have no default hotkeys.
Dark title bar changes only a title bar drawn by Windows; the active window may still use your Windows accent colour.
Apps can change the colour back. Restore this window, Restore all windows, exit, and startup recovery undo the change.

**Borderless → Borderless** removes the standard frame while preserving the client size. **Fill display** fills the
chosen display; it also accepts maximized windows and restores their maximized state on undo. Keep-size mode requires
a normal window. Hidden, cropped, rolled-up, minimized, peeking, or unresponsive windows cannot enter borderless mode.
Apps drawing their own title bars, including most browsers and terminals, are refused. Exclusive full-screen mode is
unsupported. Toggle either borderless command again to undo it. A deliberate move in keep-size mode is preserved.
While a filled window is in front, gestures pause: use the tray or an assigned hotkey to undo, or **Ctrl+Alt+R** to restore all.
Windowed games can react differently to a frame change; WinScreen does not inject into games.

With experimental effects enabled, a window's PiP menu offers **Also hide from Alt+Tab and taskbar** while
**Keep updating (hide original)** is on. Both entries are hidden together; some Windows-managed apps, including
Calculator, are refused. The source stays accessible through **Go to original window** and the tray's Views list.
Ending Keep updating, closing the view, restoring, and exiting bring the entries back. Their taskbar position can change,
and a hidden app cannot flash its taskbar button for attention. If WinScreen is force-closed, the source remains invisible
and absent from the switcher until WinScreen starts again. Restart WinScreen to recover it.
This option remains experimental; older Windows builds, mixed DPI, and multiple displays need broader validation.

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

The compact title bar appears on hover with the source title, icon, Minimize, and Close. Drag its title to move the view;
position lock also applies to title-bar dragging. Minimize suspends the picture and hides the view; restore it from the
tray's **Picture-in-Picture views → [view] → Restore view**. Its crop, position, size and options are kept.

In **Settings → Picture-in-Picture**, choose **Hover preview → Whole view** or **Reading lens** for a window view.
Rest the pointer on the picture briefly to open the preview. The lens follows the pointer within the picture;
leaving the view dismisses it. Preview takes no focus or clicks. Display and region views have no hover preview.
Hover preview is off by default, and click-through views have no hover controls.

View titles follow the original window. Clicking an Anchor reveals a source hidden by its view before bringing it forward.
If an original window is unresponsive and cannot be revealed, closing its view leaves the view open so it remains reachable.

Press **Ctrl+Alt+P** to open a live view. The original window keeps its real size.

To show something else, right-click the tray icon and choose **Picture-in-Picture views → Choose what to show**.
You can search for a window, pick a whole display, or use **Select a region** and drag out part of a display, or
click a highlighted part of an app.
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
A hidden original still appears in Alt-Tab and the taskbar unless the experimental switcher option is enabled;
use **Keep updating (hide original)** again to show it.

**Move away from text and images** looks for an emptier screen area about once per second. It stays still while the pointer
is over the view and when no better spot exists. It estimates content by contrast rather than understanding text.
While enabled, PiP is excluded from screenshots and screen sharing so it can inspect the area underneath itself.
The General setting enables this for newly opened views. While experimental effects are on, each view also has its
own menu toggle.

Selecting text directly by dragging inside PiP is not implemented. Clicking PiP first lets you interact with the original.

## Transparency and click-through

Ctrl+Alt+Up/Down adjusts opacity by the chosen step (10% by default), never below 20%. **Ctrl+Alt+O** toggles between your configured
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
- **Crop** dims the window so you can drag over the part to keep, or click a highlighted part; Esc cancels. The rest disappears and clicks there
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
- **Roll up:** an experimental effect that shrinks the window to its title bar. Choose it again to bring the height back. Restore commands and exiting also bring it back, and so does the next start after a crash.
- **Window information:** shows the window's handle, process, program file, class, position, and display.

A minimized or maximized window stays that way and gets a new place to come back to. **Restore all windows** doesn't undo
these moves, because you chose them.

### Move and resize from anywhere in a window

Turn this on in **Settings → Mouse gestures → Move and resize**; it's off by default. Hold Alt, or Win if you prefer, and drag anywhere in
a window with the left button to move it, or the right button to resize it from the nearest edge or corner. Esc while
dragging puts the window back, and **Restore prior position** undoes a drag afterwards.

This needs a system mouse hook, because a click can only be kept from an app that way. The hook exists only while the
feature is on and can act. It is removed while a game, full-screen app, or excluded program is in front, and while the PC
is locked. WinScreen doesn't hook the keyboard for this. Add apps that use Alt+drag themselves to the list on that page.

## Remember a program's setup

Adjust pinning, opacity, and dark mode, open a view if you want one, then press **Ctrl+Alt+Shift+S**. Windows belonging to that program receive the setup
the first time they come forward, including after a restart. Later manual changes stay changed.

A saved view returns with its crop, size, place, and options for the first window of that program each session; other windows open no view unless you ask. If the view keeps updating, the original hides only after you first leave it, so a window never disappears as it opens. A crop follows the window's size. A view saved on a display that's gone opens on the nearest one.

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
| Features | Lists each command and option with its shortcut and state, including changes you haven't saved. Double-click a row, or press Enter, to open its options |
| Toolbar | Places the toolbar left, center, or right of the title bar, sets the hover delay (100–2000 ms), and chooses its buttons. Selecting no buttons turns it off. The Minimize button trigger opens the toolbar below the title bar and works only on title bars drawn by Windows |

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
- **Keep updating (hide original):** the original keeps running and stays in Alt-Tab and the taskbar unless you enable the experimental option to hide both. It appears above other windows when shown again.
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
- **Roll up:** it needs a standard title bar, and apps that draw their own or enforce a minimum height may refuse.
- **Move and resize:** off by default. It doesn't apply to administrator apps or maximized windows, and pauses in full-screen apps, detected games, and excluded programs.
- **Windowed games:** a game running in a normal window can't always be detected, so add it to the exclusions. Some touchpads may not work reliably with move and resize.
- **Highlighted parts:** selection hints work only where an app reports its controls to Windows; otherwise drag by hand.
