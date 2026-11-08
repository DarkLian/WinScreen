# Workflow implementation status

Batch 1 consolidates settings and the searchable action/shortcut editor, separates tray actions by target, and fixes recovery ownership, saved opacity, explicit unlocked presets, placement validation, and input/capture teardown.

Validation on the local x64 Windows desktop:

- 375 noninteractive tests passed, including shortcut re-enable conflicts and a recovery lock held beyond the timeout.
- 37 targeted desktop tests passed for concealment ownership, PiP recovery, restore safety, placement, taskbar suspension, view options, and interactive recovery.
- All three translation catalogs have matching keys and format arguments.

Remaining acceptance work: explicit source selection and preview, keyboard region selection, packaged input/capture validation, visual checks at 100%, 150%, and 200% scaling and in high contrast, and the P2 workflows after the packaged desktop gate passes.
