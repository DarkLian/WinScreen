# Contributing to WinScreen

WinScreen is maintained by its author, [DarkLian](https://github.com/DarkLian), together with AI agents. The author
reads reports and decides what gets worked on, and the agents help build it, so writing code isn't where help is needed
most. What we can't do is use WinScreen on your computer, with your apps and your habits. That experience is the most
useful thing you can share.

**Please open an [issue](https://github.com/DarkLian/WinScreen/issues) instead of a pull request.** Describe what you
found or what you'd like, and we'll take it from there. You don't need to read the code or know how to fix anything.

If you'd also like to support the project, you can do that on [Ko-fi](https://ko-fi.com/darklian).

## What helps most

- **Your experience.** What felt confusing, slow, or awkward? What did you expect to happen? Which wording didn't make sense?
- **Suggestions.** Tell us what you're trying to get done and what would make it easier.
- **Bug reports.** Tell us about anything that broke, even if you aren't sure it's a bug.
- **Translations.** If English, 简体中文, or 繁體中文 text reads wrongly, tell us what it should say.

Small details are welcome. A clumsy label or a misplaced toolbar is worth reporting.

## What makes a report easy to act on

Include whatever you can. A short report is still better than none.

1. What you did, step by step.
2. What you expected, and what happened instead.
3. Your Windows version, and the WinScreen version if you know it.
4. Your display setup: how many monitors, and each one's scaling, such as 100% or 150%.
5. The app whose window was involved, and a screenshot or short recording if one would help.

Check that a screenshot doesn't show anything private before you post it.

## Editing translations

The catalogs are in `src/WinScreen/Localization/en.json`, `zh-Hans.json`, and `zh-Hant.json`.
They are embedded in the application; changes take effect after rebuilding and restarting WinScreen.
`Localization.cs` keeps the `UiText.Initialize(...)` and `UiText.T(...)` API. A missing translation falls back to English.

Use descriptive keys such as `settings.title`, `pip.open`, or `window.restore`. Keep a key stable when its wording changes.
For a new label, add the same key to all three files and call `T("stable.key")` where the label is created.
Helpers and label tables should receive the translated text, so all lookups remain visible to validation.
Keep program names, paths, hotkeys, and other user data outside the catalogs.

Preserve format arguments such as `{0}` and `{1}` (their order may change), intentional leading/trailing spaces,
punctuation, and menu accelerator markers such as `&Save`. Escape quotes and backslashes using standard JSON syntax.
Use UTF-8 and keep entries sorted by key. Each catalog must contain unique keys and nonempty string values.
Remove a key from all three files when its last lookup is removed.

Run the checks after editing:

```powershell
dotnet test WinScreen.slnx -c Release --filter 'FullyQualifiedName~LocalizationTests|FullyQualifiedName~LocalizationSurfaceTests'
dotnet test WinScreen.slnx -c Release --filter 'Category!=RealWindows&Category!=Interactive'
```

The first command includes isolated test windows to check Settings, menus, PiP, Anchors, and error text in all three
languages. Validation rejects missing or unused keys, duplicate keys, invalid JSON, empty translations, and changed
format arguments. The legacy-output digest test records the migration's exact output, including spaces and accelerators.
For an intentional wording change, review the catalog diff and update the affected expected digest in `LocalizationTests.cs`.
