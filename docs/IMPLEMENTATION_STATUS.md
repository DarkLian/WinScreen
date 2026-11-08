# Workflow implementation status

Changes are grouped into two commits using the repository's existing author identity.

The first batch consolidates settings and the searchable action/shortcut editor, separates tray actions by target, and fixes recovery ownership, saved opacity, explicit unlocked presets, placement validation, and input/capture teardown. Its isolated staged snapshot passed 375 noninteractive tests before the additional save-failure and watcher-retry regression tests were added.

The second batch adds explicit Window/Display/Region selection, source refresh and previews, keyboard region entry, resizable settings, named display selection under Arrange, and display/region source bookmarks. Bookmarks can retain view size and position, reopen through the picker or tray, and require a resolved-region preview when the display layout changes or the original display is missing.

Validation on the local Windows desktop:

- The pre-P2 packaged desktop gate passed all 68 targeted tests, including the browser stress case and keyboard-only region opening.
- The final combined run passed 426 of 431 checks. Five failed in `Desktop.GainForegroundRights` because the harness's injected foreground hotkey never arrived, before the tested window actions began. A focused rerun reproduced those same five harness failures.
- Earlier targeted runs passed the packaged saved-region and region-opening workflows, plus native restoration and placement checks. Both PiP and Shrink retained their mappings while a source was hung, then restored the source and cleared the mappings after unhang and retry.
- A minimum-size fixture verifies exact-placement refusal and preservation of undo history after a refused restore.
- All nine language/scale layout cases passed. Rendered images were inspected, and shortcut columns were adjusted so registration status remains visible. These tests scale control bounds and fonts; they do not change the OS monitor DPI setting.
- All three translation catalogs have matching keys and format arguments. The latest focused source/safety run passed all 13 checks, including a missing display, failed-save rollback, lock timeout, and watcher-launch retry.
- x64 publication and the ARM64 build succeeded; the ARM64 build reported zero warnings and errors. ARM64 binaries were not executed on this x64 machine.

Acceptance still requiring a suitable desktop/hardware session: the five foreground-dependent reruns, actual OS high-contrast and per-monitor DPI checks, mixed-DPI physical display moves, monitor removal/reconnect, and GPU device loss. Test reports are under `tests/WinScreen.Tests/TestResults`; layout images and the self-contained executable are under `dist` (all ignored by Git).
