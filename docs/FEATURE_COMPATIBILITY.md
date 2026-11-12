# WinScreen feature compatibility

What each WinScreen feature does with each kind of window, as the code behaves today (last checked
2026-11-12). Where the code and its tests don't settle a case, it says
**Unverified** rather than guessing.

Paths are relative to `src/WinScreen/` unless they start with `tests/`.

## Limitations at a glance

- **Systems:** Windows 10 (version 2004 or later) and Windows 11 on x64 only. ARM PCs and 32-bit Windows aren't
  supported (below).
- **Apps running as administrator,** such as Task Manager, can only be changed when WinScreen runs as administrator
  too: turn on **Settings → General → Run as administrator**.
- **Games and full-screen apps:** commands pause while one is in front, and views can't appear over games in
  exclusive full screen.
- **Views** of a minimized app show its last picture, because the app stops drawing. Protected video may show as black.
- **Smooth mode** shows Windows' yellow capture border on Windows 10; on Windows 11 WinScreen asks to leave it off.
- **Screenshots and screen sharing** don't show views of a display or region, or Anchors that move off content.
- **Dark, Glass and Crop** depend on how each app draws itself.
- **Using apps through a view** *(experimental)*:
  - Some newer controls, in Store apps and parts of File Explorer, take clicks, right-clicks and scrolling, but not
    hover, dragging or selecting.
  - Native tooltips may not show.
  - When the app asks you something, its window comes back so you can answer.
  - A few apps can't be hidden, such as PowerShell ISE.
  - Games, and apps with anti-cheat, should use **Show original** instead.
- **Enhanced hover** needs an x64 PC and a 64-bit app.
- **Snap Layouts** from Windows 11 aren't offered while moving a window with Alt+drag; WinScreen snaps to edges itself.

## Supported systems

| System | Status |
|---|---|
| Windows 10 (version 2004 or later) and Windows 11 on x64 | ✅ Supported. The only architecture that is tested, released and updated. Everything below describes it. |
| Windows 11 on ARM64 | ❌ Not officially supported; experimental. No installer, portable package or update feed is published for it. |
| The x64 build on an ARM64 PC (emulation) | ❔ Unverified. Enhanced hover is refused there. |
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
| **Desktop, taskbar, Start and other parts of Windows** | Refused: "The desktop, taskbar and other parts of Windows can't be changed." | `WindowController.Refusal`, `Windows/WindowController.cs:450` |
| **WinScreen's own windows** (views, tiles, Settings) | Refused: "Use this on the original window…" | same |
| **Programs on the leave-alone list** (Settings → Programs) | Refused with the program's name. The list wins over everything else. | same; `AppSettings.Allows` |
| **Programs not on the work-on list**, when that list isn't empty | Refused with the program's name. | same |
| **Administrator (elevated) apps**, when WinScreen isn't elevated | Windows' UIPI blocks WinScreen from tagging the window, so effects are refused with a message saying to start WinScreen as administrator. Mouse gestures check for elevation directly and leave it alone. Started as administrator, WinScreen works on them like any other app, gestures included (`ProcessInfo.SelfElevated`). | `WindowIdentity.Capture`, `Windows/WindowIdentity.cs:28`; `TrayApplication.CanGesture` |
| **App not responding** | Most changes wait half a second for an answer, then say "The window isn't responding right now." | `WindowController.EnsureResponsive` |
| **Full-screen app or game running** (Settings → General → "Pause in full screen", on by default) | Hotkeys and toolbar commands are refused, except Put back / Put every window back. Views follow their own rules (see Picture-in-Picture). | `FullScreenPolicy.Decide`, `Windows/FullScreen.cs:49` |
| **PC locked or the user is away** | The mouse hook is removed, so gestures and Ctrl+Alt+wheel stop. | `ModifierHookPolicy`, `FullScreen.IsAway` |
| **Experimental features off** | Dark, Glass, Shrink, Crop and Roll up are refused with "Turn on experimental features in Settings". Turning experimental off while they're in use undoes them. | `CommandCatalog.IsExperimental`, `Settings/AppSettings.cs:133`; `TrayApplication.ApplyPreferences` |

## Window states used in the tables

| State | How WinScreen tells |
|---|---|
| **Normal** | Visible, not minimized or maximized. |
| **Maximized** | `IsZoomed`. |
| **Minimized** | `IsIconic`. |
| **Borderless** | WinScreen's own Borderless or Fill display is on for it (`PlacementBeforeBorderless`). |
| **Fills the screen** | Covers the whole monitor and has no standard title bar: a game or a full-screen app (`WindowInfo.FillsMonitor`, `Windows/WindowController.cs:1451`). A normal maximized window doesn't count because it has a title bar. |
| **Pinned** | Kept on top by WinScreen (`TogglePin`). Windows that pin themselves are "topmost" but not pinned by WinScreen. |
| **Hidden** | Hidden by WinScreen behind a view, a tile or Keep updating (`state.Hidden`). A view's menu shows Keep updating as **Keep hidden**, under **Original window**. |
| **Click-through** | WinScreen's Click-through is on. |
| **Custom shape** | The app gives its window a region of its own (`WindowInfo.ShapeOf`). |
| **Own transparency** | The app made itself layered without WinScreen (`EnsureLayeringSafe`). |

## Window changes

| Feature | Normal | Maximized | Minimized | Borderless (WinScreen's) | Fills the screen | Pinned | Hidden by WinScreen | Click-through |
|---|---|---|---|---|---|---|---|---|
| **Pin on top** (Ctrl+Alt+T) | ✅ | ⚠️ ¹ | ❔ | ✅ | ⚠️ ² | — | ✅ | ✅ |
| **Opacity** (Ctrl+Alt+Up/Down, O) | ✅ | ✅ | ❔ | ✅ | ⚠️ ² | ✅ | ✅ ³ | ✅ |
| **Click-through** (Ctrl+Alt+C) | ✅ | ✅ | ❔ | ✅ | ⚠️ ² | ✅ | ✅ | — |
| **Send to bottom** | ✅ | ✅ | ❔ | ✅ | ⚠️ ² | ❌ ⁴ | ❌ ⁵ | ✅ |
| **Borderless / Fill display** | ✅ | ⚠️ ⁶ | ❌ | — | ❌ ⁷ | ✅ | ❌ | ❔ |
| **Dark title bar** | ✅ | ✅ | ❔ | ❔ | ⚠️ ² | ✅ | ❔ | ✅ |
| **Roll up** 🧪 | ✅ ⁸ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❔ |
| **Crop** 🧪 | ✅ | ❔ | ❔ | ❌ ⁹ | ⚠️ ² | ✅ | ❔ | ❔ |
| **Dark** 🧪 | ✅ | ✅ | ⚠️ ¹⁰ | ❔ | ⚠️ ² | ✅ | ❔ | ✅ |
| **Glass** 🧪 | ✅ | ✅ | ⚠️ ¹⁰ | ❔ | ⚠️ ² | ✅ | ❔ | ✅ |
| **Shrink to a tile** 🧪 | ✅ | ❔ | ⚠️ ¹¹ | ❔ | ⚠️ ² | ✅ | ❌ ⁵ | ❔ |
| **Put this window back** (Ctrl+Alt+U) | ✅ | ✅ | ✅ | ✅ | ✅ ¹² | ✅ | ✅ | ✅ |
| **Put every window back** (Ctrl+Alt+R) | ✅ | ✅ | ✅ | ✅ | ✅ ¹² | ✅ | ✅ | ✅ |

1. **Pause pin when maximized** (Settings → Appearance) takes the window off the top while it's maximized and puts it
   back after (`WindowController.FollowMaximized`, `Windows/WindowController.cs:418`).
2. Allowed by the window checks, but with **Pause in full screen** on, the hotkey is refused while that app is the
   full-screen one (`FullScreenPolicy`). With the pause off, it runs.
3. Changes the level that comes back when the window is shown again.
4. "Unpin this window first, then send it to the bottom." (`WindowController.SendToBottom`)
5. "Show this window first, then try again."
6. **Borderless** needs a normal window ("Show this window normally first"). **Fill display** works from maximized.
   Both also work on apps that draw their own title bar, such as browsers: Borderless keeps the window as big as it
   shows. Borderless is refused for a window with no frame at all ("This window has no frame to take away"); Fill
   display still works there. Both are refused while any game uses exclusive full screen (`WindowAppearance.cs`).
   **Dark title bar** only changes a title bar Windows draws ("This window draws its own title bar").
7. A game in exclusive full screen: "Borderless mode isn't available while a game uses exclusive full screen."
8. Needs a standard title bar, and refuses an app that keeps its own minimum height (`errors.rollUp.*`).
9. "Turn off borderless mode first." Also refused for **custom-shape** windows.
10. The effect stops drawing while the window is minimized and comes back with it (`Glass/ContentOverlay.cs:132`).
11. A minimized window is restored first, then shrunk (`Pip/ShrinkTile.cs:77`).
12. Put back always runs, even during a full-screen app.

**Own transparency** windows (the app made itself see-through): Opacity, Click-through, hiding and Glass are refused
with "This window draws its own transparency…" (`EnsureLayeringSafe`, `Windows/WindowController.cs:1301`).

## Moving and placing

| Feature | Normal | Maximized | Minimized | Snapped by WinScreen | Borderless | Fills the screen | Hidden by WinScreen | Elevated |
|---|---|---|---|---|---|---|---|---|
| **Align** (corners, edges, center) | ✅ | ⚠️ ¹ | ⚠️ ¹ | ✅ | ❔ | ⚠️ ² | ❌ | ❌ |
| **Move to next display** | ✅ | ⚠️ ¹ | ⚠️ ¹ | ✅ | ❔ | ⚠️ ² | ❌ | ❌ |
| **Place exactly** | ✅ | ⚠️ ¹ | ⚠️ ¹ | ✅ | ❔ | ⚠️ ² | ❌ | ❌ |
| **Put back earlier position** | ✅ | ✅ | ✅ | ✅ | ❔ | ⚠️ ² | ❌ | ❌ |
| **Alt+left drag (move)** | ✅ | ✅ ³ | ❌ | ✅ ⁴ | ❔ | ❌ | ❌ | ❌ |
| **Alt+right drag (resize)** | ✅ | ✅ ⁵ | ❌ | ✅ | ❔ | ❌ | ❌ | ❌ |
| **Snap at screen edges** (during Alt+left drag) | ⚠️ ⁶ | ⚠️ ⁶ | — | ⚠️ ⁶ | ❔ | — | — | — |

1. A maximized or minimized window stays that way; it's given a new place to come back to (`WindowPlacer.PlaceHidden`,
   `Windows/WindowPlacer.cs:166`).
2. Same full-screen pause as above.
3. Comes out of maximized once the pointer moves a few pixels, at its normal size, with the grabbed point still under
   the pointer. An Alt+click alone leaves it maximized. **Esc** puts it back to maximized
   (`MouseGestures.Restore` and `Finish`, `Windows/MouseGestures.cs`). Tested in
   `tests/WinScreen.Tests/ModifierDragTests.cs` (`Alt_drag_brings_a_maximized_window_out_and_esc_puts_it_back`).
4. A window WinScreen snapped goes back to its size from before the snap when it's dragged out.
5. Comes out of maximized at the same size it had on screen, then resizes from the edge nearest the press, so it doesn't
   jump. **Unverified** by a test.
6. Needs **Settings → Mouse gestures → Snap to the edges of the screen** (on by default) **and** Windows' own **Snap
   windows** setting. Left or right edge: half the screen. Corners: a quarter. Top: maximize. The bottom edge doesn't
   snap. An edge with another display past it doesn't snap, so the pointer can cross. Windows that can't be resized
   don't snap; windows without a maximize button don't maximize. Windows 11's Snap Layouts panel at the top of the
   screen can't be opened this way: there's no way for another program to show it. Snaps made this way aren't
   Windows' own snap groups, so Snap Assist doesn't offer to fill the other half.

**Alt-drag in more detail**

| Situation | Behavior | Where |
|---|---|---|
| Program on **Leave these programs alone** (gestures) | Not moved; the click reaches the app. Defaults include Blender, Maya, 3ds Max, Photoshop, Krita and GIMP. | `AppSettings.ModifierExclusions`; `TrayApplication.CanDrag` |
| App that's slow to redraw | Moves are sent one at a time; the next waits until the app has caught up (up to 50 ms), so the window can't keep moving after you let go. | `MouseGestures.Send`; tested by `A_fast_drag_of_a_slow_app_catches_up_as_soon_as_its_let_go_and_then_stays` |
| App that's hung | Moves are still asked for but not waited on. Coming out of maximized and snapping are skipped. | `IsHungAppWindow` checks in `MouseGestures` |
| **Esc**, with or without Alt or Win still held | Puts the window back where it started, maximized again if it was. | `EscapeModifiers` in `MouseGestures` |
| Windows removes the mouse hook (it does this silently when a hook is slow) | WinScreen notices input without hook activity and puts the hook back within a few seconds. A drag in progress at that moment may keep following the pointer until the next click. **Known weakness.** | `TrayApplication.FollowModifierHook`, `HookSilenceSeconds` |
| Foreground app fills the screen and isn't maximized (a game) | The hook isn't installed at all while it's in front. | `ModifierHookPolicy.ShouldRun` |
| Maximized app with an auto-hidden taskbar | Counts as maximized, not as full screen, so gestures work. | `TrayApplication.FollowModifierHook` |

## Picture-in-Picture views and tiles

| Source window | View | Notes |
|---|---|---|
| Normal | ✅ | |
| Maximized | ✅ | The view shows the maximized window. **Unverified**: how Go to original window treats it. |
| Minimized | ⚠️ | WinScreen asks first, then restores it, because Windows doesn't draw minimized windows (`TrayApplication.OpenViewOf`). |
| Hidden by WinScreen (Keep updating) | ✅ | That's how Keep updating works: the original is invisible but still drawn. |
| Shrunk, Dark or Glass | ⚠️ | Those are turned off first; if one can't be, "Show this window first". |
| Elevated | ⚠️ | Opening the view tags the window, which UIPI blocks, so it's refused (`WindowIdentity.Capture`). |
| Game in exclusive full screen | ❌ | "Views can't appear over a game in exclusive full screen." |
| Any app in full screen, with Pause in full screen on | ⚠️ | Allowed only with **Allow Picture-in-Picture in full screen** on, and only for borderless full screen (`FullScreenPolicy`, `Windows/FullScreen.cs:59`). Pressing the view hotkey in a game closes the newest view instead. |
| A window Windows won't make a thumbnail of | ❌ | "This window can't be shown in Picture-in-Picture." (`Pip/DwmThumbnailBackend.cs:22`) |
| The whole screen | ⚠️ | Needs a recent Windows 10 or 11 build ("This version of Windows can't show a view of the screen."). |

| View feature | Status | Notes |
|---|---|---|
| A click on a view | ✅ | Does nothing to the original; dragging moves the view. **Go to it** is under **Original window** in the view's menu. "Interactive PiP" is the one way to use the original through its view. |
| WinScreen's tray menu, window menu and Settings | ✅ | Tips stay above WinScreen menus and Settings, followed by the taskbar and its overflow panel, then views and other pinned windows. Layering checks repair only a covered layer, avoiding repeated lifts while nothing changes. Both supported overflow-window classes are included. Views end direct-use visits and suspend their own lifts while a WinScreen menu or the tray overflow panel is open; they pass the pointer through only to the overflow panel. A click on a view while its own menu is open closes the menu. Popup raises are queued onto the view's UI thread and checked again before use. |
| Move away from the pointer | 🧪 ⚠️ | Experimental **and** "Move out of the way" on. Needs a recent Windows (`errors.pip.autoMoveUnsupported`). |
| Keep updating, taskbar and Alt+Tab | ✅ / ⚠️ | Keep updating takes the original off the shell's task list where supported and changes nothing else about the window, so the view keeps showing its own minimize, maximize and close buttons. Its taskbar button goes, and on Windows 11 it also leaves Alt+Tab (Windows 10 Alt+Tab not yet checked). The entry comes back when the original is shown or the view closes, and after a forced exit. Store apps keep the taskbar button Windows owns; choosing it shows the original. On Windows 10 with smooth capture the button stays, to keep the view updating. The separate option that also takes a window out of Alt+Tab gives it a tool window's frame, which has a small close button only. |
| Rendering | ⚠️ | Auto, Standard (compositor thumbnail) or Smooth (screen capture). Auto tries Smooth first. Smooth falls back to Standard when it isn't available. Frame rate and scaling only apply to Smooth. |
| Yellow capture border (Smooth, and Glass) | ⚠️ | Windows can show a separate capture border. While a parked original is hidden, views use Standard capture whenever border suppression is unavailable or refused, restoring the selected backend when shown. This is necessary on Windows 10: fully off-screen parking froze the browser, and a two-pixel overlap exposed the WGC border. Visible originals and Glass retain their capture-border setting. |
| Hidden original showing through (Keep hidden, Interactive PiP) | ✅ / ⚠️ | The default parks the original at an external monitor edge with only a two-pixel strip overlapping the screen, opacity 0 and click-through. While the pointer is over the view it is at opacity 1 and can be hit, because browsers only accept a wheel turn when their own window is at its point; it goes back to opacity 0 and click-through when the pointer leaves. Input is posted at mapped child-window coordinates while the original stays still. During a visit the view shares input state with the original's thread, except while a button is held, so the app's own pointer shapes show (text, link, resize). While used directly, a view's resize edges are 2 px wide and its corners 8 px, so controls near its edges, such as a browser's tabs, can be clicked. Without Enhanced hover, apps that track the pointer leaving (most browsers) are told it left after each move, so hover effects may not hold. A view's title bar moves the view; Alt+drag also moves it. This is the only direct-use mode: the former press-replay and hover-opacity modes are removed. Placement, switcher styles and a removed taskbar button are journaled before mutation. Closing restores the original while hidden, allows repaint, then fades it in. How input reaches each window, and what it supports, is in the table below (`Pip/PipInputRoute.cs`). Presses on an app Windows marks as not responding are not sent, so they can't act later. Store apps keep the taskbar button Windows owns while hidden; if anything brings a hidden original forward (its taskbar button, Alt+Tab, the app itself), it is shown. The pointer hook only decides what to let through and never waits on the app. VM tests (Windows 10) cover mapped buttons, double-clicks, wheels, drags, keys, placement and recovery; Chrome, File Explorer and Calculator were also tested on Windows 11. |
| Controls inside the original's top bar | Conditional | Chrome toolbar/tab controls and File Explorer's modern controls were tested on Windows 11, including Explorer's Duplicate tab context-menu action. Edge toolbar cases were tested on Windows 10. Duplicate tab works, and its menu stays where it was put (see the popups row). Relocation preserves a fixed destination and the first restoration position. Other toolbar interactions remain unverified. |
| The original's popups | ✅ / ⚠️ | Display-only popups stay off screen. Interactive menus and lists are moved near the mapped pointer and clamped to its monitor, raised above the view, and take real input. A submenu opens beside its parent menu, as it would on the original, and stays above it: menus opened together are raised together, oldest first (tested with File Explorer's New submenu). On Windows 11, File Explorer's newer menus move themselves back toward the hidden original a few times while they open; WinScreen puts them back as Windows reports each move, about a millisecond later, and also moves a new one as soon as it appears (`Pip/PopupGuard.cs`). Measured over 10 openings: 2 screen refreshes showed a menu in the wrong place, against 38 before; any that remains is a flash at the edge of the screen as the menu opens. Dialogs end direct use and reveal the original. Apps with unusual ownership or custom popup behavior need manual checking. |
| The original's caption buttons in a view used directly | ✅ | By hit-test (`PressRules.CaptionActionFor`): close asks the original to close (`SC_CLOSE`) and the view goes with it, or the original is shown after a second if it's still there; minimize minimizes the view, never the original (its picture would stop); maximize goes to the original; the icon or a right-click on the title bar opens the view's menu; borders do nothing; the title bar moves the view. An app that minimizes itself from its own content is restored, still hidden, and the view minimizes; one that maximizes itself goes to the original. |
| The original after a visit | ✅ | The original never moves during a visit; it stays parked until it is shown or the view closes. |
| Parking and display changes | ✅ / ❔ | A hidden original is parked on an outer edge of its own monitor when it has one, so it keeps its scale. When the displays change (a monitor added or removed, a resolution change), it is parked again past an outer edge of the new layout and the view is kept on a screen. Tested by reproducing the change's effect on one display; real mixed-scale monitors haven't been tested. |
| Apps that refuse to be see-through | ⚠️ | Some apps (WPF ones, such as PowerShell ISE) drop the style that makes a window see-through, so they can't be hidden, kept updating or used directly. The view says so and leaves the app as it was; a view of it still works while it is on screen. Other ways to hide them were measured and don't work: off screen, behind the taskbar edge or with an empty window region, a WPF window stops drawing and its view freezes (both capture methods). Its mouse input also needs the real pointer over the spot, which posted messages can't give. |

**Apps that don't work through a view.** No single way of sending input works with every app: Windows gives real mouse and keyboard input only to the window under the pointer, and each app reads input in its own way. So every app has an answer, and it is not always the view. **Use it, then hide again**, under **Original window** in the menu of any view whose original is hidden, shows the original where it was, to be used as it is, and hides it again once another window is in use (its own dialogs and menus count as still using it). Nothing is sent to the app. Programs given **Show original** under **Settings → Picture-in-Picture → Using apps through a view** do this on a click on their view, and Interactive PiP sends them nothing. Minimizing the view, or choosing anything else about the original, leaves it shown. Verified on Windows 11 x64 with a test window: shown at its place, kept while in use, hidden and parked again after another window is activated, Interactive PiP restored.

Games are not supported through a view and none has been tested; use the original for those.

One alternative was measured and set aside (Windows 11 x64, Chrome): keeping the real window under the pointer, beneath an opaque view that lets clicks through. Real hover, moves and a right-click menu all worked there and the page kept drawing. But the original is larger than its view, and the part that sticks out can't be removed: clipping the original to the view's rectangle also clips the picture in both capture methods, and at opacity 1 of 255 every pixel under it shifts by one level, which is the faint outline seen in early versions. Its menus would also open at full size over a scaled view.

**Interactive PiP (experimental): what works where.** **Keep hidden** stays checked and unavailable while enabled; disabling Interactive PiP restores the previous hiding preference. Showing the original or recovery turns interaction off.

**Several interactive views at once.** Each view takes input only while the pointer is on it, so going straight from one to another works. Verified with two views used one after the other and back, although both hidden originals are parked in the same place:
- Two browser pages (Chrome on Windows 11 x64; Edge and Chrome on Windows 10): clicks, typing, wheel turns, drag selection, double-click selection and the page's right-click menu (opening beside the pointer) reach only the page under the pointer.
- A browser page and Calculator (a Store app, which takes input another way): clicks and keys reach only the one under the pointer (Windows 11 x64 and Windows 10).
- Enhanced hover on in two Chrome views (Windows 11 x64): the page under the pointer keeps its hover and the one just left clears it.

Not yet tried with several views: middle-click scrolling, File Explorer, three or more views, and two views of the same program's windows. In 0.9.1 and earlier, after a click the keys could go back to one of the views instead of the page, so typing was lost (`Two_pages_used_through_two_views_at_once_each_get_only_their_own_input`).

**Enhanced hover (experimental)** keeps hover effects showing while an app is used through a view, using a small native helper inside the application. It applies only with Interactive PiP, which is itself experimental and off by default. **Settings → Picture-in-Picture → Using apps through a view** is one table of apps: each row says whether views of that app start with Enhanced hover on and, for a program you add, whether a click shows its original (a program has one or the other). **Choose for me** is on by default and picks the kinds it was verified with: Chrome-family browsers, Firefox and Electron apps. File Explorer is never picked automatically because of its tab menus (below). Clearing it lets you pick each kind yourself. Each view's menu can still turn it on or off for that view, and a view follows Settings until it is changed there. Leaving the view or turning the option off clears the hover. Native tooltips remain unavailable. It does not change popup placement or fix WinUI menu flicker.

The helper is only ever used with the kinds of app below and with programs added by name to that table. Anything else, games included, is left alone. It needs Windows 10 or 11 on an x64 PC (not an ARM64 PC, even for the x64 build), the app must be 64-bit, and a missing helper leaves ordinary Interactive PiP working.

| App | Setting | Status |
|---|---|---|
| Chrome | Chrome, Edge, Brave and Vivaldi | ✅ Verified on Windows 11 x64: CSS hover and page previews hold while the pointer rests, follow it to another element, and clear on leave, turning off and suspending |
| Edge, Brave, Vivaldi | same | ❔ Enabled because they share Chrome's engine; not tested (none installed on the test desktop) |
| VS Code (Electron) | VS Code, Discord and similar apps | ✅ Verified on Windows 11 x64: hover holds in a web view, shows in the view's picture, and clears on leave and turning off |
| Discord and other Electron apps | same | ❔ Recognized by Electron's folder layout and window; not tested individually |
| File Explorer (file list) | File Explorer | ⚠️ Verified on Windows 11: a hovered row is highlighted in the view and stays so; without the option it never highlights. With the option on, the right-click menu of a tab did not take its click in testing (Duplicate tab failed three times of three), so leave it off if you use tab menus. The command bar, tabs and address bar are used through accessibility and don't get hover |
| Firefox | Firefox | ✅ Verified on Windows 11 x64: hover holds, shows in the view's picture, follows the pointer to another element, and clears on leave and turning off |
| Other programs added by name | Other programs | ❔ Depends on the app. Works only for windows that take posted mouse input; WinUI and Store content go through accessibility, and WPF apps can't be hidden at all. Don't add games that use anti-cheat |
| Windows 10 | same settings | ✅ Verified on Windows 10 x64 (12 Nov 2026) with the same tests as Windows 11: Chrome hover and page previews (also with a window maximized in front, and in two views), Firefox, VS Code, Chrome's own middle-click scrolling, and File Explorer's row highlight |
| ARM64 PCs (the ARM64 build, and the x64 build or x64 apps under emulation), 32-bit apps | — | ❌ Not available |

On x64, a hidden browser or Electron app keeps its two-pixel strip at a side edge of the screen the whole time, raised without activation, instead of along the bottom, whether or not Enhanced hover is on. Under the taskbar a browser finds itself covered: it stops drawing and stops taking pointer moves, and does not check again until it is clicked, so hover never started when the option was turned on. File Explorer and programs added by name use the side edge only while the option is on for them. Other apps, and every app on Windows 10, park along the bottom as before. Caption styles, taskbar removal, popup placement and recorded restoration state are unchanged.

The helper filters only queued mouse-leave messages for the currently hovered window on its own UI thread.

Every press gets one outcome: delivered, reached a popup, moved the view, a caption button, unsupported (that kind of window has no way to take it; nothing is sent), unconfirmed (tried, but the app was busy or refused), or starting/cancelling optional middle-click scrolling.

| Window under the pointer | Input path | Supported | Unsupported |
|---|---|---|---|
| Ordinary windows of the app (Win32, WinForms, browsers, Explorer's file list) | Posted mouse messages at the mapped point | Mapped pointer moves and pointer shape, button messages, double-click (and triple), drag and text selection, wheel, keys after a click | Hover effects drop between moves unless Enhanced hover is on for that app; tooltips remain suppressed. A browser's own middle-click scrolling (click, move away, click again) needs Enhanced hover: with it on, Chrome scrolls and stops as usual (verified on Windows 11 x64), and the view's own **Middle-click scrolling** option stands aside for browsers and Electron apps and is greyed out in the menu. Without Enhanced hover only hold-and-drag scrolls, and the view's option is the alternative |
| Newer UI frameworks: WinUI 3 islands (Explorer's command bar, tabs and address bar on Windows 11) and Store app content (Calculator, Settings) | Accessibility (UI Automation) | Click (invoke, select, toggle, expand, focus a text box); right-click opens the control's own menu, next to the pointer; double-click only where it means two clicks (buttons, check boxes, links, menu items); wheel scrolls what scrolls; keys after a click | Hover, drag and text selection, middle and side buttons, a double-click that opens an item or selects a word |
| Any other program's window inside the original | — | — | Everything |

| App (framework) | Direct use | Verified |
|---|---|---|
| Chrome, Edge (Chromium) | ✅ | Pointer shapes, clicks, double-click to select a word (also with a pixel of jitter and in a view a quarter of the page's size), drag selection, typing, toolbar, tabs, context menus, wheel |
| VS Code (Electron) | ✅ | Double-click to select a word, typing, its editor's context menu (drawn inside its window) and a click on one of its items, saving |
| Firefox | ✅ | Clicks, double-click to select a word, drag selection, typing, wheel, its context menu by the pointer |
| VLC (Qt) | ✅ | Context menu by the pointer, menu bar menus, a real click on a menu item |
| File Explorer (Win32 and WinUI 3) | ✅ | See the table above |
| Calculator, Settings (Store apps) | ✅ | Click, typing, double-click on buttons, wheel, caption buttons |
| PowerShell ISE (WPF) | ❌ | Can't be hidden or used directly (see above); a view of it works while it is on screen |

**Double-clicks.** Windows' own rule applies through a view: until the double-click time has passed, the pointer staying inside the double-click rectangle isn't a move, and the second (or third) press lands where the first did. In a view shown smaller, one pixel of hand jitter is several in the app. When the view stops sharing input with the app for a press, Windows soon tells the app where the real pointer is; the press waits until that has happened, which it can see because the view is told too (at most 100 ms). Arriving during the press, that news used to turn a double-click in browsers into two single clicks. The wait is one tick of the system timer on the Windows 11 PC tested (about 15 ms) and 31 to 57 ms in the Windows 10 VM; a press that didn't stop sharing doesn't wait.

Popups the original owns are followed whichever thread or process made them, and so are its program's own menus; other programs' windows are never moved. When the app stops answering for a moment, WinScreen stops sharing input with it until it answers again, so it never waits on the app.

Tested: Windows 10 VM (test windows, Edge, Calculator, Settings, and at 150% scale per-monitor, DPI-unaware and system-aware apps: presses, double-click, wheel, drag and title bar all land on the shown point) and Windows 11 (Chrome, File Explorer including its Duplicate tab menu, Calculator, Settings). Firefox, VS Code (Electron), VLC (Qt) and PowerShell ISE (WPF) were also checked as described above. Games and raw-input behavior remain unverified. Real multi-monitor and mixed-scale testing is deferred because hardware is unavailable; simulated display-change tests remain in place.

**Optional middle-click scrolling.** In each Interactive PiP view, enable **Middle-click scrolling** to scroll continuously by moving away from the middle-click point. Click again, left-click, press Escape or leave the view to stop. The option is off unless **Middle-click scrolling in new interactive views** is on under **Settings → Picture-in-Picture**, and it replaces the app's middle-click action only in that view. It sends wheel input through the existing route; the app may finish a short smooth-scroll animation after cancellation. Native browser autoscroll remains unreliable while parked.

## Anchors, toolbar, window menu, Ctrl+Alt+wheel and saved setups

| Feature | Normal | Maximized | Minimized | Fills the screen | Excluded | Elevated |
|---|---|---|---|---|---|---|
| **Anchor** (manual) | ✅ | ✅ | ✅ ¹ | ⚠️ ² | ❌ | ❔ |
| **Automatic Anchors** | ✅ | ✅ | ✅ ¹ | ⚠️ ² | ⚠️ ³ | ❔ |
| **Title-bar toolbar** | ✅ | ❔ | — | ❌ | ❌ | ❔ |
| **Window menu gesture** (Ctrl+right click) | ✅ | ✅ | ❌ | ❌ | ⚠️ ⁴ | ❌ |
| **Ctrl+Alt+wheel opacity** | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| **Saved setups** | ✅ ⁵ | ✅ | — | ⚠️ ² | ❌ | ❌ |

1. Clicking the Anchor brings a minimized window back (`Anchors/AnchorButton.cs:224`).
2. Paused with the rest of WinScreen while a full-screen app runs.
3. Has its own **Only** and **Never** lists on the Anchors page, on top of the main lists.
4. Has its own leave-alone list on the Mouse gestures page.
5. Applied once, the first time a main window of that program comes forward; changes after that stay. Dark from a
   saved setup needs experimental on.

## Combinations

| Combination | Behavior |
|---|---|
| Pinned + maximized, with Pause pin when maximized | Off the top while maximized, back on top when restored. |
| Pinned + Alt-dragged out of maximized | The pin stays. **Unverified** whether Pause pin when maximized puts it back on top straight away. |
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

- **Win 11 Snap Layouts panel** and Snap Assist aren't available to Alt-drag (no public API). Edge snapping is
  WinScreen's own.
- **A lost mouse hook mid-drag** can leave a window following the pointer until the next click.
- **Elevated windows** are refused by tagging, not by an up-front check. Running WinScreen as administrator lifts this,
  and with **Run as administrator** on, starting with Windows keeps those rights.
- **Borderless windows from other apps** (not WinScreen's Borderless) are treated as normal unless they fill the
  screen. Most cells marked ❔ under Borderless refer to WinScreen's own Borderless and haven't been tested.
- **Multiple monitors with different scaling**: snapping does a second pass if the margins change on the way, but
  only the primary-monitor case is tested.

Title bars are mandatory and have no whole-view tooltip. Whole-view hover previews appear only below half the shown source width, including a crop; the reading lens is unchanged. Title-bar checks passed in the Windows 10 VM. Views resize from a six-DIP edge/corner band without a native frame. Keep aspect ratio defaults on per view; turning it off fits the picture inside the chosen shape.
