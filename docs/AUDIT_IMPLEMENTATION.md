# Audit implementation and acceptance checks

[README](../README.md) · [User Guide](USER_GUIDE.md) · [Development](DEVELOPMENT.md) · [Security](../SECURITY.md)

This records the hardening work that followed a review of commit `ce715662`.

## Implemented safeguards

| Audit finding | Implementation and validation |
| --- | --- |
| Recovery ordering and lost failed entries | Write-through, flushed journal replacement precedes native mutations; save failures reject the change. Failed entries remain retryable, and independent cleanup steps continue after a failure. Successful cleanup steps retire their ownership bits. Fault-injection tests inspect the on-disk record before mutation and recover a failed crop across restart. |
| Unrelated style changes overwritten | Restore only the style bits owned by WinScreen, preserving independently added bits. Native test adds WS_EX_NOACTIVATE and verifies per-window recovery preserves it even after exclusions change. |
| Reused HWND / PID | Acquire a random native window property plus PID, thread ID and process creation time. The property disappears on window destruction. Mutation, restoration, previews, dark overlays and Anchor close requests validate identity. A replaced-token test verifies an old snapshot is not written. Legacy unverifiable records are archived rather than applied to another window. |
| Malformed preferences or journal | Normalize null collections/entries, bound settings size, skip malformed journal entries individually, and preserve originals in `.invalid-*` backups. A file with nothing usable moves aside so work can continue; one that cannot be read pauses new changes until a retry succeeds, and never prevents exit. Recovery can start with default preferences. Tests cover nulls, invalid record types, short placement arrays and backup preservation. |
| PiP misdelivery | Default click action is Go to original, with no injection. Experimental forwarding requires identity, foreground, visibility, non-click-through style and root hit-test agreement. Mapping clamps to exclusive source bounds; SendInput results are checked. Refused steps fall back safely; failed peeks roll back placement and retain failed cleanup. Tests cover safe defaults, click-through refusal and failed movement. |
| Shared hide state | Tray enforces one PiP per source and mutual exclusion with Shrink. Restore closes views/tiles before restoring the original and cancels an in-progress crop selection. |
| Settings transaction | Stage edits on a clone, reserve new hotkeys while old keys remain active, persist preferences before retiring old registrations, and show conflicts beside fields. A failed save retains live preferences/startup choices. Tests cover conflicts, failed persistence and recovery binding retention. |
| Preview geometry/rendering | Fit both dimensions to the source monitor, clamp placement and dragged views, intersect resized source crops, check DWM update results, and require successful rendering before concealment. Preserve an explicit position set before WinForms' deferred Shown event; a regression test proves the event ordering. |
| Layered rendering / hover | Reject transparency and concealment when original layered-window attributes cannot be read. Test with a native layered window, avoiding WinForms' automatic attribute writes. Opacity changes honor the current solid-on-hover state. |
| Dark / Glass | Hide dark overlays for invisible, minimized, concealed or cloaked sources. Respect neighboring z-order rather than repeatedly raising every pinned overlay to the top. Treat screen-capture errors as feature failures. Keep both features experimental and off by default. |
| Target, status and recovery UX | Tray menu snapshots and names its target, shows checked effects and a persistent last-result surface, and provides Restore this window (Ctrl+Alt+U). Recovery bypasses exclusions/full-screen command pause. Toolbar dismisses on focus, geometry or eligibility changes; hover remains optional. |
| Anchor controls and placement | Ordinary right-click/Shift+F10 context menu with explicit minimize/close/remove actions. Fit new Anchors into rows/columns on the target monitor and clamp dragging. Middle-click dismisses the Anchor even if the app cancels/hides. |
| Saved setup identity / partial failure | New records include/display executable path; legacy name matching remains until resaved. Mark successful applications only after their steps complete, and retry failed applications after a five-second delay. Experimental dark mode remains disabled until explicitly allowed. |
| UI stalls and repeated capture | Probe an already hung target with a 100 ms abort-if-hung message timeout and reduce foreground retry budget. Share a one-second screen-content cache per monitor area across auto-moving views. A hung-target test bounds rejection time; this is not a universal guarantee against a target hanging after the probe or slow storage. |
| Update release rollover | Resolve latest once to an exact stable vMAJOR.MINOR.PATCH tag; fetch feed, signature and package from that release. Require signed package versions to match the tag. Tests assert both metadata URLs use the same versioned release path. |
| Release supply chain | Pin Actions commits, use read-only permissions for builds, disable persisted checkout credentials, protect release signing through a named environment, validate stable tags, require a successful dedicated desktop run for the exact SHA, and reject unsigned/wrong-publisher installers. External certificate/environment configuration is still required. |
| Shutdown and update sequencing | Verify the running installation's executable path and lifetime before sending close, check PostMessage, and require process exit within the timeout. A normal restore failure cancels exit/update scheduling, retains records, and stays available for recovery. |
| Test naming / compatibility | -SkipInteractive now excludes both RealWindows and Interactive tests; -DesktopTests explicitly opts into native desktop tests. Document the tested build rather than claiming Windows 10 2004+ support. |

## Validation matrix and release work still required

Local verification on 2026-11-07: Release build completed with zero warnings/errors; all **153** selected logic and
native-window tests passed. The 0.5.0 self-contained installer and RSA-PSS signed update feed were generated.
The packaged-app interactive suite was not run on this shared workstation; it belongs on the dedicated disposable VM.
The NuGet transitive dependency audit reported no known vulnerable packages, and Microsoft Defender found no threats
in the generated release directory. These checks do not establish production signing or installed-upgrade compatibility.

| Environment / scenario | Status |
| --- | --- |
| Local Windows 11 Pro, build 26300, x64, one 1920×1080 display at 100% scaling | Build and 153 logic/security/settings/native disposable-window tests passed, including recovery fault injection. |
| Fast hosted Windows CI | Workflow prepared; consult this commit's Actions run for hosted verification. Local test results do not establish a hosted CI pass. |
| Dedicated signed-in Windows VM | Manual desktop workflow added with the interactive-desktop runner label; requires a separately provisioned disposable runner. |
| Production Authenticode | No trusted signing certificate/provider supplied. Local installer remains unsigned. Public release deliberately fails until signing and the expected publisher thumbprint are configured. |
| Protected environment / stable tags | Configure required reviewers for the release environment and tag protection in repository settings. Workflow code alone cannot enforce an unconfigured review policy. |
| Real install, sign-in startup, installed N→N+1 upgrade, uninstall | Requires clean standard-user VM validation, including hung shutdown and failed recovery. No live public update/install lifecycle is claimed. |
| Mixed DPI, secondary monitor, monitor disconnect, Windows 10/other editions | Not available in the current single-monitor environment; not claimed as verified support. |
| Lock/unlock, virtual desktop switches, elevated targets and overlapping topmost effects | Visibility/cloaking checks and experimental gates added; broader combinations still require dedicated manual runs. |
| Storage stalls / source freezes after the responsive probe | Original records are retained, but synchronous native/storage calls still need profiling under these races. This is an explicitly remaining responsiveness limitation. |

Dark, Glass, Shrink, Crop, auto-move and PiP input forwarding remain off by default while combination coverage is expanded.
Go to original and the ordinary pin/opacity/Anchor/recovery flows remain available without experimental effects.

## Windows contracts consulted

- [SendInput returns inserted-event count and is subject to UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
- [Layered attributes can interfere with UpdateLayeredWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes).
- [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Runtime support and this application's tested matrix are separate claims.
