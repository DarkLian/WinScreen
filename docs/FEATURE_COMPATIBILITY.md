# WinScreen feature compatibility

What each WinScreen feature does with each kind of window, as the code behaves today (last checked
2026-11-08). Where the code and its tests don't settle a case, it says
**Unverified** rather than guessing.

Paths are relative to `app/src/WinScreen/` unless they start with `tests/`.

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
| **Experimental features off** | Dark, Glass, Shrink, Crop and Roll up are refused with "Turn on experimental effects in Settings". Turning experimental off while they're in use undoes them. | `CommandCatalog.IsExperimental`, `Settings/AppSettings.cs:133`; `TrayApplication.ApplyPreferences` |

## Window states used in the tables

| State | How WinScreen tells |
|---|---|
| **Normal** | Visible, not minimized or maximized. |
| **Maximized** | `IsZoomed`. |
| **Minimized** | `IsIconic`. |
| **Borderless** | WinScreen's own Borderless or Fill display is on for it (`PlacementBeforeBorderless`). |
| **Fills the screen** | Covers the whole monitor and has no standard title bar: a game or a full-screen app (`WindowInfo.FillsMonitor`, `Windows/WindowController.cs:1451`). A normal maximized window doesn't count because it has a title bar. |
| **Pinned** | Kept on top by WinScreen (`TogglePin`). Windows that pin themselves are "topmost" but not pinned by WinScreen. |
| **Hidden** | Hidden by WinScreen behind a view, a tile or Keep updating (`state.Hidden`). |
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
   Both need a standard title bar, and both are refused while any game uses exclusive full screen
   (`WindowAppearance.cs:100`).
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
| Click through to the original | 🧪 ⚠️ | Experimental **and** "Forward clicks" on. The original slides under the pointer so the click lands on the spot shown; the pointer itself never moves, so near a screen edge part of the original goes off screen for the moment. A maximized original, or a click that can't be confirmed as landing on the original (or one of its own popups), brings the original forward instead, without a warning (`PipWindow.Interact`). |
| WinScreen's tray menu, window menu and Settings | ✅ | Order, top first: WinScreen's own hover tips; its open menus (with submenus, a view's own menu, the window menu) and Settings; the Windows taskbar; then always-on-top windows, views and their parts included. Everything WinScreen lifts to the top (views, their title bar and chip, hover previews, toolbars, pin frames, windows it pins) goes through `TopLayer.Raise`, which lifts the taskbar back over it and then the open menus and tips; while the tray menu is open a 100 ms check does the same for anything else. A view activated by a click goes back under the taskbar. A view's own menu lifts its view with it while open, because Windows keeps a menu's owner just under it. Opening the tray menu or Settings ends any "Use this view directly" visit first (`TrayApplication.EndVisits`). |
| Move away from the pointer | 🧪 ⚠️ | Experimental **and** "Move out of the way" on. Needs a recent Windows (`errors.pip.autoMoveUnsupported`). |
| Keep updating, taskbar and Alt+Tab | ✅ / ⚠️ | Keep updating automatically removes the original from the taskbar and Alt+Tab, restoring its entry when shown or the view closes. Existing refusals apply to Windows-managed taskbar buttons and smooth capture on Windows 10. Native tool-window captions may omit minimize/maximize buttons. |
| Rendering | ⚠️ | Auto, Standard (compositor thumbnail) or Smooth (screen capture). Auto tries Smooth first. Smooth falls back to Standard when it isn't available. Frame rate and scaling only apply to Smooth. |
| Yellow capture border (Smooth, and Glass) | ⚠️ | Windows can show a separate capture border. While a parked original is hidden, views use Standard capture whenever border suppression is unavailable or refused, restoring the selected backend when shown. This is necessary on Windows 10: fully off-screen parking froze the browser, and a two-pixel overlap exposed the WGC border. Visible originals and Glass retain their capture-border setting. |
| Hidden original showing through (Keep updating, Use this view directly) | ✅ / ⚠️ | The default parks the original at an external monitor edge with only a two-pixel strip overlapping the screen, opacity 0 and click-through. Input is posted at mapped child-window coordinates while the original stays still. A view's title bar moves the view; Alt+drag also moves it. Compatible input keeps the replay path for apps that read the real pointer, with a brief opacity-1 interval during presses. The former hover-opacity mode is removed. Placement and switcher styles are journaled before mutation. Closing restores the original while hidden, allows repaint, then fades it in. VM tests cover mapped buttons, double-clicks, wheels, drags, keys, placement and recovery; Windows 11 remains for manual validation. |
| The original's popups | ✅ / ⚠️ | Display-only popups stay off screen. Interactive menus and lists are moved near the mapped pointer and clamped to its monitor, raised above the view, and take real input. Dialogs end direct use and reveal the original. Apps with unusual ownership or custom popup behavior need manual checking. |
| The original's caption buttons in a view used directly | ✅ | By hit-test (`PressRules.CaptionActionFor`): close asks the original to close (`SC_CLOSE`) and the view goes with it, or the original is shown after a second if it's still there; minimize minimizes the view, never the original (its picture would stop); maximize goes to the original; the icon or a right-click on the title bar opens the view's menu; borders do nothing; the title bar moves the view. An app that minimizes itself from its own content is restored, still hidden, and the view minimizes; one that maximizes itself goes to the original. |
| The original after a visit | ✅ | Moves the app hadn't handled yet when the visit ended are undone for 600 ms afterwards (`PipWindow.HoldSettledPlace`). |

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
| View open + its source pinned or see-through | Allowed; the view shows the window as it is. |
| Experimental turned off with effects in use | Crops, tiles, Dark and Glass are undone; any that fail are listed in one notice. |
| Settings import | Applies only when you choose Apply or Save; the old settings are kept as a backup (five kept). |
| WinScreen closes or crashes with changes in place | The change journal puts windows back on the next start. Hidden windows are watched and brought back if WinScreen stops (`RecoveryWatcher`). |

## Getting a window back

| Problem | Fix |
|---|---|
| A window is invisible or can't be clicked | **Ctrl+Alt+R** (Put every window back), or the same item in the tray menu. |
| One window looks wrong | **Ctrl+Alt+U** on it, or **Put this window back** in its window menu. |
| A window moved somewhere you didn't want | **Put back earlier position** (Arrange menu) goes back up to five steps. |
| A drag went wrong | **Esc** before letting go. |
| WinScreen crashed | Start it again; it puts windows back from its journal. If the journal is damaged it says where a copy was saved. |

## Known gaps and things to check

- **Win 11 Snap Layouts panel** and Snap Assist aren't available to Alt-drag (no public API). Edge snapping is
  WinScreen's own.
- **A lost mouse hook mid-drag** can leave a window following the pointer until the next click.
- **Elevated windows** are refused by tagging, not by an up-front check. Running WinScreen as administrator lifts this;
  start-at-sign-in doesn't, because it starts WinScreen without administrator rights.
- **Borderless windows from other apps** (not WinScreen's Borderless) are treated as normal unless they fill the
  screen. Most cells marked ❔ under Borderless refer to WinScreen's own Borderless and haven't been tested.
- **Multiple monitors with different scaling**: snapping does a second pass if the margins change on the way, but
  only the primary-monitor case is tested.

Title bars are mandatory and have no whole-view tooltip. Whole-view hover previews appear only below half the shown source width, including a crop; the reading lens is unchanged. Title-bar checks passed in the Windows 10 VM. Views resize from a six-DIP edge/corner band without a native frame. Keep aspect ratio defaults on per view; turning it off fits the picture inside the chosen shape.
