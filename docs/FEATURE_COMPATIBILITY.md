# WinScreen feature compatibility

What each WinScreen feature does with each kind of window, as the code behaves today (last checked
2026-11-22, after version 1.3.0). Where the code and its tests don't settle a case, it says
**Unverified** rather than guessing.

Paths are relative to `src/WinScreen/` unless they start with `tests/`.

## Limitations at a glance

- **Systems:** Windows 10 (version 2004 or later) and Windows 11 on x64 only. ARM PCs and 32-bit Windows aren't
  supported (below).
- **Apps running as administrator,** such as Task Manager, can only be changed when WinScreen runs as administrator
  too: turn on **Settings → General → Run as administrator**.
- **Games and full-screen apps:** commands pause while one is in front, and views can't appear over games in
  exclusive full screen.
- **Views:** minimizing a window that has a view hides it behind the view instead, so the view keeps updating. It keeps
  its taskbar button and Alt+Tab entry, and picking it there brings it back (**Keep minimized original windows on the
  taskbar and in Alt+Tab**, on by default; off, Keep hidden goes on instead, PipWindow.FollowOriginalMinimized). Used
  directly from its view and then set back to Watch only, it stays minimized that way. A
  window that can't be hidden stops drawing when minimized, and its view shows the last picture. With Keep hidden off,
  a fully covered window is kept drawing too; one that can't be made see-through still stops, as browsers do when
  covered. Protected video may show as black.
- **Smooth mode** shows Windows' yellow capture border on Windows 10; on Windows 11 WinScreen asks to leave it off.
- **Screenshots and screen sharing** don't show views of a display or region, or Anchors that move off content.
- **Dark, Glass and Crop** depend on how each app draws itself.
- **Using apps through a view**: Chrome, Edge, Brave, Vivaldi, Firefox, Electron apps and File Explorer without experimental features; other apps, Enhanced hover and
  middle-click scrolling *(experimental)*:
  - Some newer controls, in Store apps and parts of File Explorer, take clicks, right-clicks and scrolling, but not
    hover, dragging or selecting.
  - Native tooltips may not show.
  - When the app asks you something, its window comes back so you can answer.
  - A few apps can't be hidden, such as PowerShell ISE.
  - Games, and apps with anti-cheat, should use **Show original** instead.
- **Enhanced hover** needs an x64 PC and a 64-bit app.
- **Snap Layouts** from Windows 11 aren't offered while moving a window with Alt+drag; WinScreen snaps to edges itself.
- **Virtual desktops:** views and Anchors show on every desktop (Anchors can be kept to their window's). A view keeps
  updating while you're on another desktop. On Windows 10 and on Windows 11 24H2 and later, a window kept hidden by a
  view goes back to its own desktop when it's shown again; that move uses an undocumented part of Windows, so elsewhere, or where it
  isn't available, the window stays on the desktop you're on.

## Supported systems

| System | Status |
|---|---|
| Windows 10 (version 2004 or later) and Windows 11 on x64 | ✅ Supported. The only architecture that is tested, released and updated. Everything below describes it. |
| Windows 11 on ARM64 | ❌ Unsupported. No installer, portable package or update feed is published for it. |
| The x64 build on an ARM64 PC (emulation) | ❌ Unsupported and not tested. Enhanced hover is refused there. |
| 32-bit Windows (x86) | ❌ Unsupported. There is no 32-bit build. |

**Why ARM64 is suspended.** There is no ARM64 device or signed-in ARM64 desktop to test on, and too little demand
to justify keeping a second architecture going. WinScreen leans on native Windows APIs, window capture, input hooks
and shell behavior, and each of those has to be checked on the architecture it runs on. Enhanced hover already works
on Windows 11 x64 only. A second set of installers, update feeds and signatures adds work to every release.

**What remains.** The source still compiles for ARM64. An automated job builds it and runs the logic tests on ARM64
hardware for every push. That job is a best-effort portability check: it opens no windows and drives no desktop, a
failure there doesn't hold up x64 work or releases, and a passing run does not show that WinScreen works on ARM64.

**Earlier ARM64 releases.** Releases up to 0.8.1 beta included ARM64 files, and those stay on the Releases page as
they were. An installed ARM64 copy gets no further updates. It can't be given an x64 update either: each architecture
reads its own update feed (`Updates/UpdateChannel.cs`), and only the x64 feed is published now.

## Key

| Mark | Meaning |
|---|---|
| ✅ Supported | Works, and a check in the code allows it. |
| ⚠️ Conditional | Works only with a setting on, in some states, or with a catch described in the notes. |
| ❌ Unsupported | WinScreen refuses on purpose and says why, or does nothing. |
| 🧪 Experimental | Only with **Settings → General → Experimental** on (`AppSettings.ExperimentalFeatures`). |
| ❔ Unverified | Nothing in the code refuses it, but no test covers it, so behavior depends on the app. |

## Rules that apply to every feature

These checks run before any feature-specific ones. If one of them refuses, nothing else is tried.

| Rule | What happens | Where |
|---|---|---|
| **Desktop, taskbar, Start and other parts of Windows** | Refused: "The desktop, taskbar and other parts of Windows can't be changed." | `WindowController.Refusal`, `Windows/WindowController.cs` |
| **WinScreen's own windows** (views, tiles, Settings) | Refused: "Use this on the original window…" | same |
| **Programs on the leave-alone list** (Settings → Programs) | Refused with the program's name. The list wins over everything else. | same; `AppSettings.Allows` |
| **Programs not on the work-on list**, when that list isn't empty | Refused with the program's name. | same |
| **Administrator (elevated) apps**, when WinScreen isn't elevated | Windows' UIPI blocks WinScreen from tagging the window, so effects are refused with a message saying to start WinScreen as administrator. Mouse gestures, the title-bar toolbar and automatic Anchors check for elevation first and leave the app alone. With **Run as administrator** on, WinScreen itself keeps running as you and starts a small helper, the same program elevated, that acts only on administrator apps' windows: a command for one of them (hotkey, menu, command line) is handed to the helper, and their toolbar, gestures, rules and Anchors are the helper's own; it has no tray icon, says what it has to say through WinScreen's, and closes with it. Each keeps its own record of what it changed, so a crash of either puts back only its own windows; drops onto a view of an administrator app stay impossible (Windows' rule). Where the helper can't run (UAC off, a standard account), or with `"AdministratorHelper": false` in settings.json, WinScreen runs as administrator as one copy, as before (`AppRoles.Reaches`, `Tray/HelperLink.cs`, `AdministratorHelperTests`, `AdministratorHelperEndToEndTests`). | `WindowIdentity.Capture`, `Windows/WindowIdentity.cs`; `TrayApplication.CanReach` |
| **App not responding** | Most changes wait half a second for an answer, then say "The window isn't responding right now." | `WindowController.EnsureResponsive` |
| **Full-screen app or game running** (Settings → General → "Pause new commands during games and full-screen apps", on by default) | Hotkeys and toolbar commands are refused, except the ones that only take something away: Put back, Put every window back, the commands for every window (below), and turning Borderless off. Views follow their own rules (see Picture-in-Picture). | `FullScreenPolicy.Decide`, `Windows/FullScreen.cs` |
| **PC locked or the user is away** | The mouse hook is removed, so gestures and Ctrl+Alt+wheel stop. | `ModifierHookPolicy`, `FullScreen.IsAway` |
| **Experimental features off** | Dark, Glass, Shrink, Crop and Roll up are refused with "Turn on experimental features in Settings". Turning experimental off while they're in use undoes them. | `CommandCatalog.IsExperimental`, `Settings/AppSettings.cs`; `TrayApplication.ApplyPreferences` |

## Window states used in the tables

| State | How WinScreen tells |
|---|---|
| **Normal** | Visible, not minimized or maximized. |
| **Maximized** | `IsZoomed`. |
| **Minimized** | `IsIconic`. |
| **Borderless** | WinScreen's own Borderless or Fill display is on for it (`PlacementBeforeBorderless`). |
| **Fills the screen** | Covers the whole monitor and has no standard title bar: a game or a full-screen app (`WindowInfo.FillsMonitor`, `Windows/WindowController.cs`). A normal maximized window doesn't count because it has a title bar. |
| **Pinned** | Kept on top by WinScreen (`TogglePin`). Windows that pin themselves are "topmost" but not pinned by WinScreen. |
| **Hidden** | Hidden by WinScreen behind a view (**Keep hidden**, under **Original window** in the view's menu) or a tile (`state.Hidden`). A view the person opens starts with it on unless **Settings → Picture-in-Picture → Hide the original window for new views** is off (`PipViewPreferences.KeepHiddenOnNewViews`); a saved view keeps its saved choice, and a window that can't be hidden (Fill display, one that refuses to be see-through) keeps its view without a message (`PipWindow.HideOriginalByDefault`, `NewViewHidingTests`). |
| **Click-through** | WinScreen's Click-through is on. |
| **Custom shape** | The app gives its window a region of its own (`WindowInfo.ShapeOf`). |
| **Own transparency** | The app made itself layered without WinScreen (`EnsureLayeringSafe`). |

## Window changes

| Feature | Normal | Maximized | Minimized | Borderless (WinScreen's) | Fills the screen | Pinned | Hidden by WinScreen | Click-through |
|---|---|---|---|---|---|---|---|---|
| **Pin on top** (Ctrl+Alt+T) | ✅ | ⚠️ ¹ | ✅ ¹³ | ✅ | ⚠️ ² | — | ✅ | ✅ |
| **Opacity** (Ctrl+Alt+Up/Down, O) | ✅ | ✅ | ✅ ¹³ | ✅ | ⚠️ ² | ✅ | ✅ ³ | ✅ |
| **Click-through** (Ctrl+Alt+C) | ✅ | ✅ | ✅ ¹³ | ✅ | ⚠️ ² | ✅ | ✅ | — |
| **Send to bottom** | ✅ | ✅ | ✅ ¹³ | ✅ | ⚠️ ² | ✅ ⁴ | ❌ ⁵ | ✅ |
| **Borderless / Fill display** | ✅ | ⚠️ ⁶ | ✅ ¹⁸ | — | ❌ ⁷ | ✅ | ❌ | ✅ |
| **Dark title bar** | ✅ | ✅ | ✅ ¹³ | ❌ ¹⁴ | ⚠️ ² | ✅ | ✅ | ✅ |
| **Roll up** 🧪 | ✅ ⁸ | ✅ ¹⁹ | ✅ ¹⁹ | ❌ | ❌ | ✅ | ❌ | ✅ |
| **Crop** 🧪 | ✅ ¹⁵ | ✅ ¹⁵ | ⚠️ ¹⁷ | ❌ ⁹ | ⚠️ ² | ✅ | ❌ ⁵ | ✅ |
| **Dark** 🧪 | ✅ | ✅ | ⚠️ ¹⁰ | ⚠️ ¹⁶ | ⚠️ ² | ✅ | ❌ ⁵ | ✅ |
| **Glass** 🧪 | ✅ | ✅ | ⚠️ ¹⁰ | ⚠️ ¹⁶ | ⚠️ ² | ✅ | ❌ ⁵ | ✅ |
| **Shrink to a tile** 🧪 | ✅ | ✅ | ⚠️ ¹¹ | ❌ ⁹ | ⚠️ ² | ✅ | ❌ ⁵ | ✅ |
| **Put this window back** (Ctrl+Alt+U) | ✅ | ✅ | ✅ | ✅ | ✅ ¹² | ✅ | ✅ | ✅ |
| **Put every window back** (Ctrl+Alt+R) | ✅ | ✅ | ✅ | ✅ | ✅ ¹² | ✅ | ✅ | ✅ |

1. **Pause pinning while a pinned window is maximized** (Settings → Appearance) takes the window off the top while it's maximized and puts it
   back after (`WindowController.FollowMaximized`, `Windows/WindowController.cs`).
2. Allowed by the window checks, but with **Pause new commands during games and full-screen apps** on, the hotkey is refused while that app is the
   full-screen one (`FullScreenPolicy`). With the pause off, it runs.
3. Changes the level that comes back when the window is shown again.
4. The window stops being kept on top first: WinScreen's pin comes off, and a window that keeps itself on top
   loses that until it's put back (`WindowController.SendToBottom`).
5. "Show this window first, then try again." A hidden window is parked out of sight, so Crop, Dark and Glass,
   which work from what is on screen, wait for it to be shown.
6. **Borderless** needs a normal window ("Show this window normally first"). **Fill display** works from maximized.
   Both also work on apps that draw their own title bar, such as browsers: Borderless keeps the window as big as it
   shows. Borderless is refused for a window with no frame at all ("This window has no frame to take away"); Fill
   display still works there. Both are refused while any game uses exclusive full screen (`WindowAppearance.cs`).
   **Dark title bar** only changes a title bar Windows draws ("This window draws its own title bar").
7. A game in exclusive full screen: "Borderless mode isn't available while a game uses exclusive full screen."
8. Needs a standard title bar, and refuses an app that keeps its own minimum height (`errors.rollUp.*`).
9. "Turn off borderless mode first." Crop is also refused for **custom-shape** windows.
10. The effect stops drawing while the window is minimized and comes back with it (`Glass/ContentOverlay.cs`).
11. A minimized window is restored first, then shrunk (`Pip/ShrinkTile.cs`).
12. Put back always runs, even during a full-screen app.
13. The window stays minimized, and the change is there when it comes back.
14. Borderless takes the title bar away, so there is none to darken.
15. Some apps put their own shape straight back (Windows Forms ones with a frame, for example). Crop then says
    the app draws its own shape, rather than showing as on. Taking a crop off a maximized window leaves it
    maximized.
16. Picture-keeping Dark and layered Glass hide the window under their picture, so they say "Turn off
    borderless mode first." Colour Glass and Dark mode's plain filter work, and the filter follows the window.
17. The window comes back first, so the part to keep can be chosen over it. A crop made while it's minimized (by
    a saved setup or recovery, say) is there when it comes back.
18. The window comes back first, as its own Restore would bring it back. One that comes back maximized then follows
    note 6.
19. The window comes back to its usual size first, then rolls up. One that was maximized is maximized again when
    it's rolled down.

**Own transparency** windows (the app made itself see-through): Opacity, Click-through, hiding and Glass are refused
with "This window draws its own transparency…" (`EnsureLayeringSafe`, `Windows/WindowController.cs`).

**When something else undoes a change.** WinScreen keeps checking the windows it changed. When the app itself or
another program undoes a change, it either puts it back or lets go of it, and says so; it never fights forever.

| What happened | What WinScreen does | Where |
|---|---|---|
| Another program took "always on top" from a pinned window | Pins it again. If it is taken more than three times within ten seconds, WinScreen lets go of the pin and says "Another program keeps taking…". It leaves the window alone while it covers its display (the app's own full screen, unless WinScreen filled the display with it), is minimized, isn't answering, or while the pin is paused for a maximized window. | `WindowController.StillPinned` |
| The app rewrote its own transparency or click-through | Seen on two checks in a row (a moment in passing doesn't count), WinScreen lets go of its opacity, Glass colour and click-through on that window and says so. Put back then leaves the app's own transparency alone. | `WindowController.StillLayered` |
| The app took the crop off itself | The window is no longer shown as cropped. Otherwise a crop keeps its rectangle through a resize. | `WindowController.StillCropped` |
| A window with Dark or Glass stops taking clicks | Four times a second, each window with a look is asked where a press at its middle would go. If presses keep going elsewhere for a second, that look is turned off and the person is told. A cropped window is asked at the middle of the part it keeps. Windows that are cloaked, on another virtual desktop, made click-through or hidden behind a tile or a view aren't judged, nor a spot where something of the person's is above the window. | `Windows/PressWatch.cs` |
| Fade range, Glass levels for each app or window, and areas kept as they are (Glass and Dark mode) | All need a graphics card: on a PC without one, Glass makes one colour see-through and Dark mode uses its plain filter, and none of these settings change them. Kept areas are saved per app and scaled with the window's display scaling. With fading Glass off, an app with kept areas gets plain Glass drawn by the graphics card instead of the colour-key kind; its see-through page then takes clicks rather than passing them to what is behind. | `Glass/ContentOverlay.cs`, `Glass/ContentShaders.cs`, `Settings/KeptAreaPreferences.cs` |
| A window with Glass has a video behind it (Settings → Backgrounds) | It plays while the window shows and pauses while the window is minimized or cloaked, as on another virtual desktop. Sound, when turned up, comes from one window at a time: the last one in front whose background has sound. Changing the fit or the volume doesn't start the video over. A file that can't be opened is reported once, and Glass shows without it. | `Glass/ContentEffects.cs`, `Glass/ContentOverlay.cs`, `Glass/Backdrop.cs` |

**Window information** shows a window's program, file, process and size, with a button to open its folder. It
follows the same rules as the other window changes.

**Commands for every window** (tray menu, or a hotkey if one is set) don't depend on any one window. They only take
something away, so they also work while a full-screen app runs (`CommandCatalog.Everywhere`):

| Command | What it does |
|---|---|
| **Turn off click-through everywhere** | Clicks reach every window and view again. Shown only while one is click-through. |
| **Unpin all windows** | Takes every WinScreen pin off. Shown only while a window is pinned. |
| **Hide all Anchors** | Hides every Anchor until chosen again or WinScreen restarts. |
| **Hide the toolbar for now** | Hides the title-bar toolbar until chosen again or WinScreen restarts. |

## Moving and placing

| Feature | Normal | Maximized | Minimized | Snapped by WinScreen | Borderless | Fills the screen | Hidden by WinScreen | Elevated |
|---|---|---|---|---|---|---|---|---|
| **The nine places** (corners, edges, center) | ✅ | ✅ ¹ | ⚠️ ¹ | ✅ | ✅ | ⚠️ ² | ❌ | ⚠️ ⁸ |
| **Move to next display** | ✅ | ✅ ¹ | ⚠️ ¹ | ✅ | ✅ | ⚠️ ² | ❌ | ⚠️ ⁸ |
| **Exact position and size** | ✅ | ✅ ¹ | ⚠️ ¹ | ✅ | ✅ | ⚠️ ² | ❌ | ⚠️ ⁸ |
| **Back to previous position** | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ ² | ❌ | ⚠️ ⁸ |
| **Alt+left drag (move)** | ✅ | ✅ ³ | ❌ | ✅ ⁴ | ✅ | ❌ | ❌ | ⚠️ ⁸ |
| **Alt+right drag (resize)** | ✅ | ✅ ⁵ | ❌ | ✅ | ✅ | ❌ | ❌ | ⚠️ ⁸ |
| **Snap at screen edges** (during Alt+left drag) | ⚠️ ⁶ | ⚠️ ⁶ | — | ⚠️ ⁶ | ❌ ⁷ | — | — | — |

1. A minimized window stays minimized; it's given a new place to come back to (`WindowPlacer.PlaceHidden`,
   `Windows/WindowPlacer.cs`). A maximized window comes back to its usual size in the place chosen, except with
   Move to next display (or Move to display), which keeps it maximized on the new display. Between displays, tested in
   `tests/WinScreen.Tests/WindowPlacementTests.cs` with a virtual second display; not yet on real multi-monitor hardware.
2. Same full-screen pause as above.
3. Comes out of maximized once the pointer moves a few pixels, at its normal size, with the grabbed point still under
   the pointer. An Alt+click alone leaves it maximized. **Esc** puts it back to maximized
   (`MouseGestures.Restore` and `Finish`, `Windows/MouseGestures.cs`). Tested in
   `tests/WinScreen.Tests/ModifierDragTests.cs` (`Alt_drag_brings_a_maximized_window_out_and_esc_puts_it_back`).
4. A window WinScreen snapped goes back to its size from before the snap when it's dragged out.
5. Comes out of maximized at the same size it had on screen, then resizes from the edge nearest the press, so it doesn't
   jump. Tested in `tests/WinScreen.Tests/ModifierDragTests.cs`.
6. Needs **Settings → Mouse gestures → Snap to the edges of the screen** (on by default) **and** Windows' own **Snap
   windows** setting. Left or right edge: half the screen. Corners: a quarter. Top: maximize. The bottom edge doesn't
   snap. An edge with another display past it doesn't snap, so the pointer can cross. Windows that can't be resized
   don't snap; windows without a maximize button don't maximize. Windows 11's Snap Layouts panel at the top of the
   screen can't be opened this way: there's no way for another program to show it. Snaps made this way aren't
   Windows' own snap groups, so Snap Assist doesn't offer to fill the other half. Snapping to the outer edge of a
   second display at another scale (150% beside 100%) lands on half of that display; tested with a virtual second
   display.
7. Borderless takes the resize frame away, so the window doesn't snap; it stays where it's let go. Alt+drag still
   moves it and Alt+right drag still resizes it.
8. Only when WinScreen runs as administrator too (**Settings → General → Run as administrator**).

**Alt-drag in more detail**

| Situation | Behavior | Where |
|---|---|---|
| Program on **Leave these programs alone** (gestures) | Not moved; the click reaches the app. Empty by default. | `AppSettings.ModifierExclusions`; `TrayApplication.CanDrag` |
| App that's slow to redraw | Moves are sent one at a time; the next waits until the app has caught up (up to 50 ms), so the window can't keep moving after you let go. | `MouseGestures.Send`; tested by `A_fast_drag_of_a_slow_app_catches_up_as_soon_as_its_let_go_and_then_stays` |
| App that's hung | Moves are still asked for but not waited on. Coming out of maximized and snapping are skipped. | `IsHungAppWindow` checks in `MouseGestures` |
| **Esc**, with or without Alt or Win still held | Puts the window back where it started, maximized again if it was. | `EscapeModifiers` in `MouseGestures` |
| Windows removes the mouse hook (it does this silently when a hook is slow) | WinScreen notices input without hook activity and puts the hook back within a few seconds. A drag in progress at that moment ends where the window was last moved, as if let go there. | `TrayApplication.FollowModifierHook`, `MouseGestures.Reinstall`; tested by `A_hook_lost_in_the_middle_of_a_drag_is_put_back_and_the_next_drag_works` |
| Foreground app fills the screen and isn't maximized (a game) | The hook isn't installed at all while it's in front. | `ModifierHookPolicy.ShouldRun` |
| Maximized app with an auto-hidden taskbar | Counts as maximized, not as full screen, so gestures work. | `TrayApplication.FollowModifierHook` |

## Picture-in-Picture views and tiles

| Source window | View | Notes |
|---|---|---|
| Normal | ✅ | |
| Maximized | ✅ | The view shows the maximized window. **Go to it** brings it forward still maximized and in its place, whether it was shown or hidden. |
| Minimized | ⚠️ | WinScreen asks first, then restores it, because Windows doesn't draw minimized windows (`TrayApplication.OpenViewOf`). |
| Hidden by WinScreen (Keep hidden) | ✅ | That's how Keep hidden works: the original is invisible but still drawn. |
| Shown (Keep hidden off) and fully covered | ✅ / ⚠️ | Browsers and Electron apps stop drawing once nothing of their window shows. While another window covers all of it, the original is made see-through and click-through and put above the rest, where it can't be seen either, so it goes on drawing; it keeps its place, its taskbar button and Alt+Tab, and Keep hidden stays off. As soon as a part of it would show, it goes back under the windows still over it. Picked or brought forward by its app, it comes to the front as usual. When the window in front is minimized (its button or the taskbar) and Windows passes the see-through original over for a window below it, the original gets the focus, as it would have; while another window still covers it, the focus goes on to the next window. One that stops answering stays see-through until it answers, without holding WinScreen up. Nothing is lifted while an app is full screen. On minimizing the window over it, what's under the original showed for one look in 144, 14 ms in (Windows 11). A popup of its own appearing (it would go up with it) puts it back. It isn't done for a window WinScreen pinned, filled, cropped, rolled up or drew with Glass, one that refuses to be see-through, or one Windows won't put on top because it was never used; those stop drawing while covered as before, and nothing is said. Recorded first, so a crash puts the window back (`WindowController.LiftWhileCovered`, `PipWindow.Cover.cs`, `CoverLiftTests`: a covered Chrome keeps drawing, measured against the same Chrome freezing without it). |
| Shrunk, Dark or Glass | ⚠️ | Those are turned off first; if one can't be, "Show this window first". |
| Elevated | ❌ | Refused with a message saying to start WinScreen as administrator, unless it already runs that way (`WindowIdentity.Capture`). |
| Game in exclusive full screen | ❌ | "Views can't appear over a game in exclusive full screen." |
| Any app in full screen, with Pause new commands during games and full-screen apps on | ⚠️ | Allowed only with **Allow Picture-in-Picture in full screen** on, and only for borderless full screen (`FullScreenPolicy`, `Windows/FullScreen.cs`). Pressing the view hotkey in a game closes the newest view instead. |
| A window Windows won't make a thumbnail of | ❌ | "This window can't be shown in Picture-in-Picture." (`Pip/DwmThumbnailBackend.cs`) |
| A whole display, or a region of one | ✅ | The view keeps itself out of screen capture so it never shows itself; that's also why screenshots and screen sharing leave it out. If Windows refuses that, the view isn't opened ("This version of Windows can't show a view of the screen."). |

| View feature | Status | Notes |
|---|---|---|
| A click on a view | ✅ | Does nothing to the original; dragging moves the view. **Go to it** is under **Original window** in the view's menu. "Interactive PiP" is the one way to use the original through its view. |
| WinScreen's tray menu, window menu and Settings | ✅ | From the top down: WinScreen's tips, its menus and Settings, the taskbar and its hidden-icons panel, then views and other pinned windows. While a WinScreen menu or the hidden-icons panel is open, views stop taking input and stay below it. A click on a view while its own menu is open closes the menu. |
| Move away from text and images | 🧪 ⚠️ | Experimental. On from the view's menu, or for new views with **Move Picture-in-Picture views away from text and images** in Settings → Picture-in-Picture. The view leaves itself out of screen capture so it can see what is under it, so it doesn't show in screenshots; where Windows can't do that, it says so (`errors.pip.autoMoveUnsupported`). |
| Keep hidden, taskbar and Alt+Tab | ✅ / ⚠️ | Keep hidden takes the original off the shell's task list where supported and changes nothing else about the window, so the view keeps showing its own minimize, maximize and close buttons. Its taskbar button goes, and it also leaves Alt+Tab, on Windows 10 and 11. The entry comes back when the original is shown or the view closes, and after a forced exit. Store apps keep the taskbar button and Alt+Tab entry Windows owns; picking either shows the original, a quick Alt+Tab included (`StoreAppInputTests.A_hidden_store_app_picked_from_the_taskbar_or_switcher_is_shown_and_its_taskbar_button_stays`, real clicks and keys). On Windows 10 with smooth capture the button stays, to keep the view updating. The separate option that also takes a window out of Alt+Tab gives it a tool window's frame, which has a small close button only. |
| Other virtual desktops | ✅ / ⚠️ | A view shows on every desktop. Many apps stop drawing while their window's desktop isn't shown, so while you're on another desktop the view hides its original for that time and shows it again when you're back (`Pip/AwayFromDesktop.cs`). The view's menu says "Hidden while you're on another desktop" meanwhile; it isn't Keep hidden, so a saved setup doesn't keep it. A desktop switch doesn't change a view's Keep hidden or Interactive, and Go to it from another desktop lands on the window. It doesn't do this when something else already hid the original. An original that the view hid and that is shown again from another desktop goes back to its own (`Native/VirtualDesktopsInternal.cs`): this uses an undocumented Windows interface, each move is checked afterwards, and where it can't be done the window stays where Windows put it. |
| Rendering | ⚠️ | Auto, Standard (compositor thumbnail) or Smooth (screen capture). Auto tries Smooth first. Smooth falls back to Standard when it isn't available. Frame rate and scaling only apply to Smooth. Scaling **Best** (the default) shows the window's own pixels at its own size. Down to two thirds of that it uses a sharper filter kept within the pixels each view pixel covers, and smaller still it averages them. When bigger it enlarges along edges and sharpens within the nearest pixels. Either way text and lines stay crisp without halos. On Windows' software renderer, or a graphics card without compute shaders, it averages and blends instead. **Pixelated** keeps whole pixels when bigger and averages when smaller; **Soft** blends, and averages below half size. Dragging an edge or turning the wheel stops at the window's own size when it comes close (`PipShaders`, `PipEnlargeTests`, `PipScalingRenderingTests`). |
| Title bar, size and hover preview | ✅ | Every view has a title bar. A view resizes from its edges and corners and keeps the picture's shape; with **Keep aspect ratio** off for that view, the picture fits inside the shape chosen. The hover preview (Settings) shows either a larger picture of the whole view, only while the view is shown at under half its source's width (or its crop's), or a magnifier around the pointer. Checked in the Windows 10 VM. |
| Rotate or mirror a view | ✅ / ⚠️ | Under **Picture**: quarter turns and mirroring, for window and region views, kept in presets. A view made smaller to fit the screen when turned has its own size again when turned back (`WgcPipTests.A_view_made_smaller_to_fit_when_turned_has_its_size_again_turned_back`). Clicks, drags, typing, the wheel, crop by dragging and zoom land where the view shows them; the app's own menus and the input method's list stay upright. It needs Smooth capture, so it is unavailable while a view shows Windows' own picture, and on Windows 10 for a view whose original is hidden (see the capture border row below); there the view shows upright. The hover preview stays upright. Checked on Windows 11 x64 (drawing in all eight orientations, presses in a turned view used directly, as administrator and not) and in the Windows 10 VM (drawing, shape, presets) (`PipTurnRenderingTests`, `InteractiveViewTests.In_a_turned_view_a_press_lands_on_the_point_the_view_shows`, `WgcPipTests.A_turned_view_swaps_its_sides_keeps_its_turn_in_a_preset_and_waits_for_capture`). |
| Copy picture from a view | ✅ | Under **Picture**, or its key. Copies what the view shows (crop, zoom, turn) at the window's own pixels, with covers black, as PNG and as a picture; also saved to Windows' Screenshots folder unless Settings → Picture-in-Picture says copy only. Taken from the view's capture, or from the window itself while Windows' thumbnail draws the view, never from the screen: windows over the view, its opacity, and display or region views (left out of screenshots) make no difference. A minimized view is brought back by the key. Checked on Windows 11 x64 (`ViewPictureEndToEndTests`, `ViewPictureTests`). |
| Freeze a copy | ✅ | A still of the view in a window of its own beside it, covers already black. Move, wheel to resize, Ctrl+drag to crop, Ctrl+wheel to zoom; copy, save or read its text from its menu (`ViewPictureEndToEndTests`). |
| Copy text from a view | ✅ / ⚠️ | Drag over a part or click for all of it; Windows' own text recognition for the person's languages (none installed: WinScreen says how to add one). Chinese is joined without spaces. Covered parts are never read. Also read through Glass with an animated background behind the text, from a region view over the window (a window with Glass can't have a view of its own). Small or stylized text may be read wrongly. Checked on Windows 11 x64 (`ViewPictureEndToEndTests`). |
| Record a view | ✅ / ⚠️ | Under **Picture**, or its key. An MP4 (H.264, 30 frames a second) of what the view shows: crop, zoom and turn as shown, covers black in every frame. Made from the view's own capture, never the screen, so windows over the view, its opacity, and display or region views make no difference. The video keeps the size of its first frame; a later change of shape (crop, zoom, turn, the window resized) is scaled into it with black around it. A view in Standard drawing records by capture while it records, then goes back. It stops and saves when stopped, minimized or closed, when capture starts again (a drawing setting changed, another display, Retry, a fallback) and when WinScreen exits; a red dot on the view while it records, and a note to click for the file after. Saved to Screen Recordings in Videos unless Settings → Picture-in-Picture names another folder. The app's own sound can be recorded too (a setting, off at first; Windows' process loopback; display and region views have none). ⚠️ Windows 10 can't record a view while its original is hidden or out of Alt+Tab (the item is greyed and says why); Windows 11 can while Windows lets the capture border be hidden. A hardware encoder is used once it has finished a test file; Windows' own encoder otherwise. Checked on Windows 11 x64 (`ViewRecordingEndToEndTests`, `ViewRecordingTests`). |
| Cover parts of a view | ✅ | Blacked out of the view's own picture: copies, frozen copies, the text reader and sharing the view's window in a call never have them. Kept through zoom, crop and their reset, turn and mirror, Freeze, Keep hidden, Use it then hide again, Interactive, opacity, minimizing, Standard and Smooth drawing and trying capture again; saved with presets and saved sources. Up to eight a view. Checked on Windows 11 x64 (`ViewPictureEndToEndTests`, `ViewCoverRenderingTests`). The hover preview and lens leave them out too. |
| Yellow capture border (Smooth, and Glass) | ⚠️ | Smooth capture can make Windows draw a yellow border around the captured window. While the original is hidden, a view uses Standard capture wherever that border can't be turned off (Windows 10), so no border shows at the screen edge, and goes back to the chosen mode when the original is shown. Visible originals and Glass keep the border setting. |
| Hidden original showing through (Keep hidden, Interactive PiP) | ✅ / ⚠️ | The hidden original is parked at an outer edge of the screen, with only a two-pixel strip on screen, fully see-through and letting clicks pass. While the pointer is over the view, that strip becomes just barely visible to the pointer, because browsers only take a wheel turn when their own window is under it; it goes back once the pointer leaves. Clicks, keys and wheel turns are sent to the matching point in the original, which never moves. The app's own pointer shapes show (text, link, resize). While a view is used directly its resize edges are thin, so controls near its edges, such as a browser's tabs, can be clicked; its title bar, or Alt+drag, moves the view. Without Enhanced hover, apps that watch for the pointer leaving (most browsers) are told it left after each move, so hover effects may not hold. A press waits up to a quarter of a second for an app that is busy for a moment (Word is, just after a click); presses on an app that isn't responding are not sent, so they can't act later. A hidden original is shown only when you pick it: **Go to it**, or, where it is still listed, its taskbar button or thumbnail or Alt+Tab (a Store app; other originals are off both). Anything else that gives it the focus leaves it hidden and passes the focus to the window that would have had it: a window minimized or closed, the view minimized, the view's own title bar or menu, the taskbar's hidden-icons panel, an app bringing itself forward (a notification or a link opening in it). The view still shows it live, and the app's own dialogs still bring it out (`ViewState.OriginalArrived`, `PipWindow.PickedFromTaskbarOrSwitcher`, `NewViewHidingTests`, `ViewStateTests`). Until 1.1.2 it was the other way round, shown unless the view recognised the hand-off, and each one it didn't recognise turned Keep hidden off. A hidden original is never disabled: a disabled Chrome stops drawing its video (`HiddenOriginalFocusTests`). An app's new window that takes a parked original's place as its own normal size (Chrome opens one where its last was) is moved to the original's home, so un-maximizing it doesn't put it past the screen's edge (`WindowController.SiblingHome`, `PipParkingGeometryTests`). Everything WinScreen changes is recorded first, so a crash puts the window back. How input reaches each kind of window is in the table below (`Pip/PipInputRoute.cs`). Tested in the Windows 10 VM (buttons, double-clicks, wheels, drags, keys, parking and recovery) and on Windows 11 with Chrome, File Explorer and Calculator; the hiding, drag and drop tests also run with WinScreen as administrator. |
| Controls inside the original's top bar | ⚠️ | Chrome's toolbar and tabs and File Explorer's newer controls were tested on Windows 11, including Explorer's Duplicate tab in a tab's menu, which stays where it was put (see the popups row). Edge's toolbar was tested on Windows 10. Other apps' toolbars are unverified. |
| The original's popups | ✅ / ⚠️ | Display-only popups stay off screen, and between visits so does any popup the app keeps open (Chrome's tab card stayed in sight; `SourcePopupPlacementTests.A_popup_the_app_keeps_open_after_a_visit_goes_off_the_screen_until_the_next`). Interactive menus and lists are moved near the mapped pointer and clamped to its monitor, raised above the view, and take real input. A submenu opens beside its parent menu, as it would on the original, and stays above it: menus opened together are raised together, oldest first (tested with File Explorer's New submenu). On Windows 11, File Explorer's newer menus move themselves back toward the hidden original a few times while they open; WinScreen puts them back as Windows reports each move, about a millisecond later, and also moves a new one as soon as it appears (`Pip/PopupGuard.cs`). Measured over 10 openings: 2 screen refreshes showed a menu in the wrong place, against 38 before; any that remains is a flash at the edge of the screen as the menu opens. Dialogs end direct use and reveal the original. Apps with unusual ownership or custom popup behavior need manual checking. |
| The original's caption buttons in a view used directly | ✅ | By hit-test (`PressRules.CaptionActionFor`): close asks the original to close (`SC_CLOSE`) and the view goes with it, or the original is shown after a second if it's still there; minimize minimizes the view, never the original (its picture would stop); maximize goes to the original; the icon or a right-click on the title bar opens the view's menu; borders do nothing; the title bar moves the view. An app that minimizes itself from its own content is restored, still hidden, and the view minimizes; one that maximizes itself goes to the original. |
| The original after a visit | ✅ | The original never moves during a visit; it stays parked until it is shown or the view closes. |
| A page or video going full screen | ✅ | A browser remembers where its window was when a page goes full screen, and goes back there afterwards. For a page that went full screen through the view that spot is the parked one, past the edge of the screen, so the window is watched after the view closes: when it leaves full screen and lands out of reach, it goes back to where it was before the view (`Windows/WindowReturn.cs`). While a page is in full screen the browser covers its screen, and moves straight back if moved, so the hidden window stays wholly see-through there instead of the barely visible state it has past the edge while the view is in use; when the page leaves full screen it is hidden past the edge again, and its return to a maximized window doesn't count as a request to show it (`WindowController.FollowParkedPlace`, `Windows/WindowParking.cs`). Verified on Windows 11 with Chrome and in the Windows 10 VM with Edge: full screen before or through the view, left through the view or after it closed (`FullScreenPageTests`). |
| Parking and display changes | ✅ / ❔ | A hidden original is parked on an outer edge of its own monitor when it has one, so it keeps its scale. When the displays change (a monitor added or removed, a resolution change), it is parked again past an outer edge of the new layout and the view is kept on a screen. Tested by reproducing the change's effect on one display; real mixed-scale monitors haven't been tested. |
| Apps that refuse to be see-through | ⚠️ | Some apps (WPF ones, such as PowerShell ISE) drop the style that makes a window see-through, so they can't be hidden, kept updating or used directly. The view says so and leaves the app as it was; a view of it still works while it is on screen. Other ways to hide them were measured and don't work: off screen, behind the taskbar edge or with an empty window region, a WPF window stops drawing and its view freezes (both capture methods). Its mouse input also needs the real pointer over the spot, which posted messages can't give. |

**Apps that don't work through a view.** No single way of sending input works with every app: Windows gives real mouse and keyboard input only to the window under the pointer, and each app reads input in its own way. So every app has an answer, and it is not always the view. **Use it, then hide again**, under **Original window** in the menu of any view whose original is hidden, shows the original where it was, to be used as it is, and hides it again once another window is in use (its own dialogs and menus count as still using it). Nothing is sent to the app. Programs given **Show original** under **Settings → Picture-in-Picture → Using apps through a view** do this on a click on their view, and Interactive PiP sends them nothing. Java apps always do, listed or not. When a click through a view doesn't get through, because it is one that can't be sent (such as a middle click on a Store app's button) or because the app missed two in a row, a note at the bottom of the view says so for a few seconds; clicking it does what **Use it, then hide again** does. From the third such note for the same app while WinScreen runs, the note offers to give that app **Show original**. Tested on Windows 10 (VM) and Windows 11 with Calculator's middle click, an app that wasn't responding, and a click on the note during direct use. Minimizing the view, or choosing anything else about the original, leaves it shown. Verified on Windows 11 x64 with a test window: shown at its place, kept while in use, hidden and parked again after another window is activated, Interactive PiP restored.

Games are not supported through a view and none has been tested; use the original for those.

One alternative was measured and set aside (Windows 11 x64, Chrome): keeping the real window under the pointer, beneath an opaque view that lets clicks through. Real hover, moves and a right-click menu all worked there and the page kept drawing. But the original is larger than its view, and the part that sticks out can't be removed: clipping the original to the view's rectangle also clips the picture in both capture methods, and at opacity 1 of 255 every pixel under it shifts by one level, which is the faint outline seen in early versions. Its menus would also open at full size over a scaled view.

**Interactive PiP: what works where.** For Chrome, Edge, Brave, Vivaldi, Firefox, Electron apps and File Explorer, **Interactive** is in every view's menu, with experimental features on or off: clicks, double-clicks, drag selection, typing, the wheel and the page's right-click menu were checked on Windows 10 (Chrome, Edge, Brave, Vivaldi, Firefox, Electron apps and File Explorer) and Windows 11 (Chrome, Brave, Vivaldi, Firefox, Electron apps and File Explorer), and the app comes back as it was when WinScreen is ended during a visit (the table below names the tests). For every other app it is experimental. Enhanced hover and middle-click scrolling stay experimental for every app; turning experimental features off while a view of one of these apps is used directly keeps it in use without them, and ends direct use of any other view. **Keep hidden** stays checked and unavailable while enabled; disabling Interactive PiP restores the previous hiding preference. Showing the original or recovery turns interaction off.

**Interactive PiP with an administrator window in front** (WinScreen not running as administrator) ⚠️. While an administrator app has the focus, Windows tells WinScreen's mouse hooks nothing, so a view can't follow the pointer: hovering it does nothing (no hover effects) and the view doesn't start a visit (`PipWindow.AdministratorInFront`, log `view.visitWaitsForPress`). The first left or right press on the picture goes to the app and brings it forward (`GivePressToVisit`), and from then on it works as usual. Before 1.4.1-beta.8 each move started a visit that ended within 150 ms ("the hook stopped seeing the pointer"), so hover flickered and a press between visits dragged the view; the gesture hook was also reinstalled every 2 s. Ctrl+press still selects on the view. Tests: `InteractiveViewTests.With_an_administrator_window_in_front_…` (2) and `Brought_back_from_the_taskbar_with_an_administrator_window_in_front_…`, Windows 11 host.

**Several interactive views at once.** Each view takes input only while the pointer is on it, so going straight from one to another works. Verified with two views used one after the other and back, although both hidden originals are parked in the same place:
- Two browser pages (Chrome on Windows 11 x64; Edge and Chrome on Windows 10): clicks, typing, wheel turns, drag selection, double-click selection and the page's right-click menu (opening beside the pointer) reach only the page under the pointer.
- A browser page and Calculator (a Store app, which takes input another way): clicks and keys reach only the one under the pointer (Windows 11 x64 and Windows 10).
- Enhanced hover on in two Chrome views (Windows 11 and Windows 10 x64): the page under the pointer keeps its hover and the one just left clears it.
- Three views at once (test windows): clicks and keys reach only the window under the pointer, in any order.
- Two windows of one program, sharing a process and a UI thread, each in its own view: clicks and keys reach only the window under the pointer.
- File Explorer in two views: a double-click on a folder opens it only in the window under the pointer.
- Middle-click scrolling in one view stops when the pointer goes on into another view, and the other window never scrolls.

The last four were checked on Windows 10 and Windows 11. In 0.9.1 and earlier, after a click the keys could go back to one of the views instead of the page, so typing was lost (`Two_pages_used_through_two_views_at_once_each_get_only_their_own_input`).

**Typing with an input method.** The hidden original has the keys while it is used through a view, so Windows opens an input method's list of choices, and the emoji panel (Win+.), by its caret, at the edge of the screen. On Windows 11 WinScreen moves Windows' own list and panel next to the caret as the view shows it, keeps them there while they page and open again, and a click in them picks as usual without ending the visit. A press anywhere else in the view while the emoji panel is open closes it, as it would elsewhere on the screen. Checked on Windows 11 with Microsoft Bopomofo in Chrome (`Typing_chinese_through_a_view_brings_the_list_beside_its_caret_and_a_click_there_picks`, `The_emoji_panel_opened_through_a_view_comes_up_beside_its_caret`). Other input methods' own windows, and Windows 10, are left as they are: their list still opens at the edge of the screen.

**Enhanced hover (experimental)** keeps hover effects showing while an app is used through a view, using a small native helper inside the application. It applies only with Interactive PiP and only with experimental features on, also for the apps above, which can be used through a view without them. **Settings → Picture-in-Picture → Using apps through a view** is one table of apps: each row says whether views of that app start with Enhanced hover on and, for a program you add, whether a click shows its original (a program has one or the other). **Choose for me** is on by default and picks the kinds it was verified with: Chrome-family browsers, Firefox and Electron apps. File Explorer is never picked automatically because of its tab menus (below). Clearing it lets you pick each kind yourself. Each view's menu can still turn it on or off for that view, and a view follows Settings until it is changed there. Leaving the view or turning the option off clears the hover. Native tooltips remain unavailable. It does not change popup placement or fix WinUI menu flicker.

The helper is only ever used with the kinds of app below and with programs added by name to that table. Anything else, games included, is left alone. It needs Windows 10 or 11 on an x64 PC (not an ARM64 PC, even for the x64 build), the app must be 64-bit, and a missing helper leaves ordinary Interactive PiP working.

| App | Setting | Status |
|---|---|---|
| Chrome | Chrome, Edge, Brave and Vivaldi | ✅ Verified on Windows 11 x64: CSS hover and page previews hold while the pointer rests, follow it to another element, and clear on leave, turning off and suspending |
| Edge | same | ✅ Verified on Windows 10 x64 with the same test as Chrome |
| Brave, Vivaldi | same | ✅ Verified on Windows 10 and Windows 11 x64 with the same test as Chrome |
| VS Code (Electron) | VS Code, Discord and similar apps | ✅ Verified on Windows 11 x64: hover holds in a web view, shows in the view's picture, and clears on leave and turning off |
| Discord and other Electron apps | same | ✅ Recognized by the files in their folder, not by name. Verified on Windows 10 and Windows 11 x64 with Electron's own download running a test page under a made-up name: the same hover test as Chrome passes. Each app's own pages are not tested individually |
| File Explorer (file list) | File Explorer | ⚠️ Verified on Windows 11: a hovered row is highlighted in the view and stays so; without the option it never highlights. With the option on, the right-click menu of a tab did not take its click in testing (Duplicate tab failed three times of three), so leave it off if you use tab menus. The command bar, tabs and address bar are used through accessibility and don't get hover |
| Firefox | Firefox | ✅ Verified on Windows 11 x64: hover holds, shows in the view's picture, follows the pointer to another element, and clears on leave and turning off |
| Other programs added by name | Other programs | ⚠️ Depends on the app. Verified on Windows 10 and Windows 11 x64 with a test window that watches for the pointer leaving: added by name, its view starts with Enhanced hover on, and the window isn't told the pointer left while the pointer stays on the view (without it, it was told 16 times in the same moves). A 32-bit program added by name is left alone. Works only for windows that take posted mouse input; WinUI and Store content go through accessibility, and WPF apps can't be hidden at all. Don't add games that use anti-cheat |
| Windows 10 | same settings | ✅ Verified on Windows 10 x64 (12 Nov 2026) with the same tests as Windows 11: Chrome hover and page previews (also with a window maximized in front, and in two views), Firefox, VS Code, Chrome's own middle-click scrolling, and File Explorer's row highlight |
| ARM64 PCs (the ARM64 build, and the x64 build or x64 apps under emulation), 32-bit apps | — | ❌ Not available |

On x64, a hidden browser or Electron app keeps its two-pixel strip at a side edge of the screen the whole time, raised without activation, instead of along the bottom, whether or not Enhanced hover is on. Under the taskbar a browser finds itself covered: it stops drawing and stops taking pointer moves, and does not check again until it is clicked, so hover never started when the option was turned on. This is the same on Windows 10 and Windows 11. File Explorer and programs added by name use the side edge only while the option is on for them. Other apps park along the bottom.

The helper filters only queued mouse-leave messages for the currently hovered window on its own UI thread.

Every press gets one outcome: delivered, reached a popup, moved the view, a caption button, unsupported (that kind of window has no way to take it; nothing is sent), unconfirmed (tried, but the app was busy or refused), or starting/cancelling optional middle-click scrolling.

| Window under the pointer | Input path | Supported | Unsupported |
|---|---|---|---|
| Ordinary windows of the app (Win32, WinForms, browsers, Explorer's file list) | Posted mouse messages at the mapped point | Mapped pointer moves and pointer shape, button messages, double-click (and triple), drag and text selection, wheel, keys after a click | Hover effects drop between moves unless Enhanced hover is on for that app; tooltips remain suppressed. A browser's own middle-click scrolling (click, move away, click again) needs Enhanced hover: with it on, Chrome scrolls and stops as usual (verified on Windows 11 x64), and the view's own **Middle-click scrolling** option stands aside for browsers and Electron apps and is greyed out in the menu. Without Enhanced hover only hold-and-drag scrolls, and the view's option is the alternative |
| A file dragged out of File Explorer's file list | The real window, under the pointer | A press on a file or folder waits: let go, it is a click; moved past Windows' drag distance, the window is put under the pointer nearly see-through, at its own size when not maximized (at most three quarters of the screen each way; a smaller size lays its menus and ribbon out differently), the file is found again there by name, the window moves to bring it under the hand and it is pressed on its name, and the drag is Windows' own: onto another window, the desktop or another view (which hands its window over). The window never shows: once Windows carries the drag it goes back out of sight, so it can't cover where the drag goes, also for a quick hand (it can't be dropped back into the same window). Empty space still selects through the view. The view stays out of the way until the drag is let go, then comes back where it was, whether the drop worked or not (`PipWindow.PickUpDrag`, `ExplorerInputTests.A_file_is_dragged_out_of_a_view`, also 200 ms from press to drop) | Dragging out of other apps (a browser's links and pictures): not yet |
| A link dragged out of a browser page (Chrome, Edge, Brave, Vivaldi) | The real window, under the pointer | A left press while the page shows its hand pointer (a link) waits: let go, it is a click; moved past Windows' drag distance, the page is put at its own size with the pressed spot under the pointer, nearly see-through, is pressed there, and goes back out of sight as soon as Windows carries the drag, without showing, so it can't cover where the drag goes (it can't be dropped back into the same page). The view stays out of the way until the drag is let go. Text still selects through the view; a click on anything with the hand pointer reaches the page on the release, with the press. Nothing is asked of the page (`PipInteraction` hand pointer, `PipWindow.FollowDrop`, `BrowserInputTests.A_link_is_dragged_out_of_a_view_into_another_window`, Windows 11: Chrome, Brave, Vivaldi) | Pictures and other things a page lets you drag without a hand pointer |
| Something dragged onto a view while WinScreen runs as administrator as one copy (with the helper, views of ordinary apps aren't elevated and take drops the usual way) | The real window, under the pointer | Windows doesn't let a drag from a window running normally reach one running as administrator, so the view is never asked. A left button pressed outside WinScreen, held while the pointer rests on the picture, while Windows' drag and drop holds the mouse (its own window, `CLIPBRDWNDCLASS`, has the capture on the thread of the window pressed, which needn't be in front: File Explorer doesn't come forward for a press on a file; any other thread of that program counts too, looked at every quarter second) is taken as a drag arriving; a button merely held (a selection box, moving a window) isn't. The original comes up as for any drop (`PipWindow.FollowDragArriving`, `ExplorerInputTests.A_file_dragged_from_another_explorer_window_lands_in_an_explorer_view` run as administrator, with a held button that must not count) | Drags that don't use Windows' drag and drop |
| Newer UI frameworks: WinUI 3 islands (Explorer's command bar, tabs and address bar on Windows 11) and Store app content (Calculator, Settings) | Accessibility (UI Automation) | Click (invoke, select, toggle, expand, focus a text box); right-click opens the control's own menu, next to the pointer; double-click only where it means two clicks (buttons, check boxes, links, menu items); wheel scrolls what scrolls; keys after a click | Hover, drag and text selection, middle and side buttons, a double-click that opens an item or selects a word |
| Any other program's window inside the original | — | — | Everything |

**How WinScreen recognises each kind of app.** These two tables come from the list WinScreen itself uses, and a test fails if they ever disagree with it. "Tested on" counts only the tests named beside it.

<!-- app-profiles: made from src/WinScreen/Compatibility/AppProfile.cs by AppProfileTests; don't edit -->
| Kind of app | Recognised by | Used through a view | Enhanced hover | A click shows the original | Tested on | Tests |
|---|---|---|---|---|---|---|
| Chrome and Edge | Program `chrome` or `msedge` | Yes | On with Choose for me | — | Windows 10 and 11 | `A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys`, `A_double_click_selects_a_word_in_a_page_used_through_a_view`, `Two_pages_in_two_views_take_drags_double_clicks_and_menus_each_on_its_own`, `A_browser_scrolls_under_the_wheel_in_a_view_used_directly`, `A_browser_view_is_used_directly_without_experimental_features_and_never_gets_the_helper`, `Enhanced_hover_keeps_css_previews_and_dom_state_and_clears_on_leave_and_disable`, `A_browsers_own_middle_click_scrolling_works_with_enhanced_hover`, `A_link_is_dragged_out_of_a_view_into_another_window`, `A_browser_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit`, `A_hook_owner_crash_restores_a_parked_browser_and_clears_hover` |
| Brave and Vivaldi | Program `brave` or `vivaldi` | Yes | On with Choose for me | — | Windows 10 and 11 | `A_browser_used_through_a_view_shows_its_own_pointer_and_takes_clicks_selection_and_keys`, `A_double_click_selects_a_word_in_a_page_used_through_a_view`, `Two_pages_in_two_views_take_drags_double_clicks_and_menus_each_on_its_own`, `A_browser_scrolls_under_the_wheel_in_a_view_used_directly`, `A_browser_view_is_used_directly_without_experimental_features_and_never_gets_the_helper`, `Enhanced_hover_keeps_css_previews_and_dom_state_and_clears_on_leave_and_disable`, `A_link_is_dragged_out_of_a_view_into_another_window`, `A_browser_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit` |
| Firefox | Program `firefox` | Yes | On with Choose for me | — | Windows 10 and 11 | `Firefox_takes_clicks_and_a_double_click_through_a_view`, `Firefox_takes_a_drag_selection_through_a_view`, `Firefox_takes_typing_through_a_view`, `Firefox_scrolls_under_the_wheel_and_opens_its_menu_by_the_pointer_through_a_view`, `Firefox_keeps_css_hover_when_chosen_in_settings`, `Firefox_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit` |
| File Explorer | Program `explorer` and window class `CabinetWClass` | Yes | Off unless chosen | — | Windows 10 and 11 | `A_hovered_row_stays_highlighted_in_the_view_only_with_enhanced_hover`, `Double_clicking_a_folder_in_a_view_navigates_explorer`, `A_submenu_opens_beside_its_menu_and_takes_the_click_through_a_view`, `A_file_is_renamed_by_typing_through_a_view`, `A_drag_selects_files_through_a_view`, `The_wheel_scrolls_the_file_list_through_a_view`, `A_file_is_dragged_out_of_a_view`, `Explorer_tab_menus_remain_stationary_during_sustained_navigation`, `File_explorer_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit` |
| The Windows shell: desktop and taskbar | Program `explorer` | With experimental features on | Never | — | A rule, checked without a window | `A_program_is_one_of_the_known_kinds_or_listed_or_left_alone` |
| Electron apps, such as VS Code and Discord | Window class `Chrome_WidgetWin_1` and Electron's files beside the program | Yes | On with Choose for me | — | Windows 10 and 11 | `An_electron_app_keeps_css_hover_when_chosen_in_settings`, `An_electron_app_known_only_by_its_folder_keeps_css_hover`, `An_electron_app_takes_clicks_and_a_double_click_through_a_view`, `An_electron_app_takes_a_drag_selection_through_a_view`, `An_electron_app_takes_typing_through_a_view`, `An_electron_app_scrolls_under_the_wheel_and_opens_its_menu_by_the_pointer_through_a_view`, `An_electron_editor_takes_a_double_click_typing_and_its_context_menu_through_a_view`, `An_electron_app_used_without_experimental_features_comes_back_when_WinScreen_is_ended_during_a_visit` |
| Java apps, such as IntelliJ IDEA | A window class starting `SunAwt` | — | Not used: nothing is sent to it | Always | Windows 11 | `IntelliJ_shows_its_original_on_a_click_on_its_view_and_works_there` |
| Store apps, such as Calculator and Settings | Program `ApplicationFrameHost` and window class `ApplicationFrameWindow` | With experimental features on | Only if added by name | — | Windows 10 and 11 | `Calculator_adds_through_a_view_takes_keys_and_says_what_it_cannot_do`, `A_hidden_store_app_picked_from_the_taskbar_or_switcher_is_shown_and_its_taskbar_button_stays` |

| Window inside an app | Window class | Used through | Tested on | Tests |
|---|---|---|---|---|
| WinUI 3 content in a desktop window, such as File Explorer's command bar | `Microsoft.UI.Content.DesktopChildSiteBridge` | Accessibility | Windows 11 | `Explorer_tab_menus_remain_stationary_during_sustained_navigation` |
| Store app content | `Windows.UI.Core.CoreWindow` | Accessibility | Windows 10 and 11 | `Calculator_adds_through_a_view_takes_keys_and_says_what_it_cannot_do` |
| The Store app frame's input window | `ApplicationFrameInputSinkWindow` | Accessibility | Windows 10 and 11 | `The_wheel_scrolls_a_store_app_through_a_view` |
<!-- app-profiles end -->

| App (framework) | Direct use | Verified |
|---|---|---|
| Chrome, Edge (Chromium) | ✅ | Pointer shapes, clicks, double-click to select a word (also with a pixel of jitter and in a view a quarter of the page's size), drag selection, typing, toolbar, tabs, context menus, wheel |
| VS Code (Electron) | ✅ | Double-click to select a word, typing, its editor's context menu (drawn inside its window) and a click on one of its items, saving |
| Firefox | ✅ | Clicks, double-click to select a word, drag selection, typing, wheel, its context menu by the pointer |
| VLC (Qt) | ✅ | Context menu by the pointer, menu bar menus, a real click on a menu item |
| File Explorer (Win32 and WinUI 3) | ✅ | See the table above |
| Calculator, Settings (Store apps) | ✅ | Click, typing, double-click on buttons, wheel, caption buttons |
| PowerShell ISE (WPF) | ❌ | Can't be hidden or used directly (see above); a view of it works while it is on screen |
| Word (Microsoft 365) | ⚠️ | A click puts the caret there, typing, saving, its right-click menu. A double-click to select a word doesn't work: Word ignores a double-click that isn't made with the real mouse, even with its window on screen. For that, give Word **Show original** (see "Apps that don't work through a view" above) |
| Excel (Microsoft 365) | ⚠️ | A click picks the cell, typing, saving, its right-click menu. A double-click to edit a cell doesn't work, because Excel won't start editing in a cell while its window is hidden past the edge of the screen. Dragging doesn't work either: Excel checks that the mouse button is really held down on its own window. For these, give Excel **Show original** (see "Apps that don't work through a view" above) |
| IntelliJ IDEA (Java) | ❌ | Can't be used through a view: Java apps ignore clicks while their window is hidden past the edge of the screen, so a view of any Java app (recognised by its window, `PipOriginalApps.IsJavaWindow`) sends it nothing: a click shows the original, as **Show original** does, without adding it in Settings. Verified on Windows 11: the click shows the editor, which then takes a real double-click and typing (`IntelliJ_shows_its_original_on_a_click_on_its_view_and_works_there`). Rider uses the same Java interface and is expected to behave the same; it wasn't tested on its own |
| LTspice (Win32) | ✅ | Its menu bar: a click on a menu opens it beside the view (a standard menu bar's menus are opened as its access key would open them, since such a bar only follows a button that is really down over it). Its right-click menu |
| Claude, ChatGPT (Electron) | ✅ | Recognised as Electron apps; pointer moves and the right-click menu through the view, and the window goes back where it was. ChatGPT was also checked with Enhanced hover on. Only an app already open was used: nothing was typed into it |

**Double-clicks.** Windows' own rule applies through a view: until the double-click time has passed, the pointer staying inside the double-click rectangle isn't a move, and the second (or third) press lands where the first did. In a view shown smaller, one pixel of hand jitter is several in the app. When the view stops sharing input with the app for a press, Windows soon tells the app where the real pointer is; the press waits until that has happened, which it can see because the view is told too (at most 100 ms). Arriving during the press, that news used to turn a double-click in browsers into two single clicks. The wait is one tick of the system timer on the Windows 11 PC tested (about 15 ms) and 31 to 57 ms in the Windows 10 VM; a press that didn't stop sharing doesn't wait.

Popups the original owns are followed whichever thread or process made them, and so are its program's own menus; other programs' windows are never moved. When the app stops answering for a moment, WinScreen stops sharing input with it until it answers again, so it never waits on the app.

Tested: Windows 10 VM (test windows, Edge, Calculator, Settings, and at 150% scale per-monitor, DPI-unaware and system-aware apps: presses, double-click, wheel, drag and title bar all land on the shown point) and Windows 11 (Chrome, File Explorer including its Duplicate tab menu, Calculator, Settings). Firefox, VS Code (Electron), VLC (Qt) and PowerShell ISE (WPF) were also checked as described above. Word, Excel, IntelliJ, LTspice and the Claude and ChatGPT apps were checked on Windows 11, with throwaway files where anything was saved (`InstalledAppTests.cs`, run with `WINSCREEN_TEST_INSTALLED_APPS=1`); a standard menu bar is also tested on a test window in the VM (`MenuBarViewTests.cs`). Not yet tested: apps that weren't installed (Spotify, Apple Music, YouTube Music, MATLAB). Games and raw-input behavior remain unverified. Real multi-monitor and mixed-scale testing is deferred because hardware is unavailable; simulated display-change tests remain in place.

**Optional middle-click scrolling.** In each Interactive PiP view, enable **Middle-click scrolling** to scroll continuously by moving away from the middle-click point. Click again, left-click, press Escape or leave the view to stop. The option is off unless **Middle-click scrolling in new interactive views** is on under **Settings → Picture-in-Picture**, and it replaces the app's middle-click action only in that view. It sends wheel input through the existing route; the app may finish a short smooth-scroll animation after cancellation. Native browser autoscroll remains unreliable while parked.

## Anchors, toolbar, window menu, Ctrl+Alt+wheel and saved setups

| Feature | Normal | Maximized | Minimized | Fills the screen | Excluded | Elevated |
|---|---|---|---|---|---|---|
| **Anchor** (manual) | ✅ | ✅ | ✅ ¹ | ⚠️ ² | ❌ | ❌ ⁶ |
| **Automatic Anchors** | ✅ | ✅ | ✅ ¹ | ⚠️ ² | ⚠️ ³ | ❌ ⁶ |
| **Title-bar toolbar** | ✅ | ✅ | — | ❌ | ❌ | ❌ ⁶ |
| **Window menu gesture** (Ctrl+right click) | ✅ | ✅ | ❌ | ❌ | ⚠️ ⁴ | ⚠️ ⁷ |
| **Ctrl+Alt+wheel opacity** | ✅ | ✅ | ❌ | ❌ | ❌ | ⚠️ ⁷ |
| **Saved setups** | ✅ ⁵ | ✅ | — | ⚠️ ² | ❌ | ❌ |

1. Clicking the Anchor brings a minimized window back (`Anchors/AnchorButton.cs`).
2. Paused with the rest of WinScreen while a full-screen app runs.
3. Has its own **Only** and **Never** lists on the Anchors page, on top of the main lists.
4. Has its own leave-alone list on the Mouse gestures page.
5. Applied once, the first time a main window of that program comes forward; changes after that stay. Dark from a
   saved setup needs experimental on.
6. Unless WinScreen runs as administrator too. Then the toolbar isn't shown on the app and no automatic Anchor is
   made for it, as mouse gestures leave it alone; a manual Anchor says to start WinScreen as administrator. Tested
   with a real administrator app on Windows 11.
7. Only when WinScreen runs as administrator too.

**Apps that draw their own title bar.** The toolbar asks the window what is under the pointer, so an app that draws its whole top row and answers "content" there never gets it. A saved setup's **Toolbar where the title bar usually is** (under **Change**) shows it for that program's windows where a title bar usually is, without asking. It is a choice, since such an app may have its own controls up there; it can't be had with **No toolbar on these windows**. Checked on Windows 11 with a test window that answers "content" everywhere: the toolbar comes with the setup and not without it (`AppEndToEndTests.A_rule_brings_the_toolbar_to_an_app_that_draws_its_own_title_bar`).

## Combinations

| Combination | Behavior |
|---|---|
| Pinned + maximized, with Pause pinning while a pinned window is maximized | Off the top while maximized, back on top when restored. |
| Pinned + Alt-dragged out of maximized | The pin stays. With Pause pinning while a pinned window is maximized, it is back on top within a moment while it's the window in use; Alt+drag doesn't switch to the window, so one dragged from behind comes back on top once it's used. |
| Click-through + pinned | Allowed, and the easiest way to lose a window: you can't click it. Ctrl+Alt+C again, or Ctrl+Alt+R, puts it back. |
| Borderless + Crop, Hide, Roll up or a view | Refused: "Turn off borderless mode first." |
| Cropped / rolled up / hidden + Borderless | Refused: "Show this window normally first." |
| Shrunk + a view of the same window | The tile is closed first, then the view opens. |
| Dark or Glass + a view of the same window | The effect is turned off first. |
| Dark mode + Glass | Never both: turning one on turns the other off first. If the new one fails or is refused (a window that is dark already), the old one is put back (`TrayApplication.SwitchLook`). |
| Arrange (a place or exact position) + maximized | The window comes back to its usual size in the place chosen; Back to previous position maximizes it again. |
| View open + its source pinned or see-through | Allowed; the view shows the window as it is. |
| Experimental turned off with effects in use | Crops, tiles, Dark and Glass are undone; any that fail are listed in one notice. |
| Settings import | Applies at once; the old settings are kept as a backup (five kept). Other changes in Settings apply as they are made, once they settle; a shortcut that is taken goes back to the one in use, and the rest still applies. |
| WinScreen closes or crashes with changes in place | The change journal puts windows back on the next start. Hidden windows are watched and brought back if WinScreen stops (`RecoveryWatcher`). |

## Getting a window back

| Problem | Fix |
|---|---|
| A window is invisible or can't be clicked | **Ctrl+Alt+R** (Put every window back), or the same item in the tray menu. |
| One window looks wrong | **Ctrl+Alt+U** on it, or **Put this window back** in its window menu. |
| A window moved somewhere you didn't want | **Back to previous position** (Arrange window) goes back up to five steps. |
| A drag went wrong | **Esc** before letting go. |
| WinScreen crashed | Start it again; it puts windows back from its journal. If the journal is damaged it says where a copy was saved. |

## Known gaps and things to check

- **Moving a window back to its own virtual desktop** relies on an undocumented Windows interface, checked against
  the documented answer after every move. It is used only on Windows 10 (22H2) and Windows 11 24H2 and later, the versions it was tried on;
  elsewhere, or if a Windows update stops it, the window stays on the desktop you're on, as before 1.1.0.
- **Win 11 Snap Layouts panel** and Snap Assist aren't available to Alt-drag (no public API). Edge snapping is
  WinScreen's own.
- **Elevated windows:** most changes find out they can't reach one when they try, and then say so; gestures, the
  toolbar and automatic Anchors check first and stay away. Running WinScreen as administrator lifts this, and with
  **Run as administrator** on, starting with Windows keeps those rights.
- **Borderless windows from other apps** (not WinScreen's Borderless) are treated as normal unless they fill the
  screen.
- **Multiple monitors with different scaling**: snapping does a second pass if the margins change on the way. Tested
  on a virtual second display at another scale; real mixed-scale monitors haven't been tested.
