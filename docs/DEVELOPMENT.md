# Development and releases

[README](../README.md) · [User Guide](USER_GUIDE.md) · [Security](../SECURITY.md) · [Public files](PUBLIC_FILES.md)

## Build

Use the .NET 10 SDK pinned in `global.json` on Windows. `build.ps1` prefers the SDK under
`%LOCALAPPDATA%\Microsoft\dotnet` if installed there.

```powershell
.\build.ps1 -SkipInteractive
```

This builds the solution, runs logic and real-window tests, publishes a self-contained app, and creates
`dist\release\WinScreen-win-Setup.exe`. Each run replaces the generated `dist\win-x64` and `dist\release` directories.

| Command | Additional behavior |
| --- | --- |
| `.\build.ps1` | Also runs the packaged app end to end; briefly uses the mouse and keyboard |
| `.\build.ps1 -SkipInteractive` | Leaves out that final interactive app test |
| `.\build.ps1 -CI` | Runs logic tests only, suitable for a build server |
| `.\build.ps1 -Version 0.4.3` | Overrides the version in `Directory.Build.props` |
| `.\build.ps1 -SkipInteractive -SignUpdates` | Also signs the update feed with the local signing key |

The default update repository is `DarkLian/WinScreen`. Override with `-UpdateRepository owner/repository` when building
a deliberately separate distribution. The repository is embedded in the binary, not read from runtime settings.

To run only the logic/settings/security tests with an SDK on PATH:

```powershell
dotnet test WinScreen.slnx -c Release --filter "Category!=RealWindows&Category!=Interactive"
```

Real-window tests create disposable windows. The Anchor regression tests send native mouse messages to their own
test windows without moving the user's pointer, including targets that cancel closing or hide instead of closing.

## Publish signed updates

The public repository is [DarkLian/WinScreen](https://github.com/DarkLian/WinScreen).
Before publishing a release, configure its Actions secret **WINSCREEN_UPDATE_SIGNING_KEY** with the matching private PEM key.
Without it the release workflow fails before publication. Never put this key in a commit, issue, release asset, or workflow log.

The public verification key is committed at `src/WinScreen/Updates/update-public-key.pem` and embedded in the app.
The initial private key was generated outside the workspace at
`%LOCALAPPDATA%\WinScreenSigning\update-private-key.pem`, with an ACL granting only its Windows user access.
Back it up securely. The local signing tool reads that file; CI reads the repository secret. A mismatched key is rejected.

Push a stable version tag such as **v0.4.3**, using a higher version for each release. The Release workflow:

1. Builds with the tag's version and embeds the hosting repository.
2. Packages the application and signs the exact `releases.win.json` bytes using RSA-PSS/SHA-256.
3. Publishes the installer, full package, feed, detached signature, and generated portable release asset together.

The updater requires these assets in the release:

| Asset | Purpose |
| --- | --- |
| `WinScreen-win-Setup.exe` | Manual installation and upgrades |
| `WinScreen-VERSION-full.nupkg` | Complete update package |
| `releases.win.json` | Version, identity, size, and SHA-256 metadata |
| `releases.win.json.sig` | Signature of the exact feed bytes |

Do not edit the feed after signing. The updater uses the latest non-prerelease GitHub release and tags of the form
`vVERSION`. It ignores drafts/prereleases and refuses equal or older versions. Keep the public key stable for existing
clients; rotation requires a transition release signed by the old key and containing the new public key.

The signing helper lives in `tools/WinScreen.UpdateSigning`. Its `init` command refuses to overwrite existing keys.
Do not regenerate the established key just to build from a fresh checkout; use the securely backed-up signing key
or the configured CI secret. Unsigned developer builds can still be built and tested without that secret.

Signing update metadata is separate from Windows Authenticode signing. The installer is currently not Authenticode signed.
See [Security](../SECURITY.md) for verification requirements and remaining risks.

## Repository layout

| Folder | Responsibility |
| --- | --- |
| `src/WinScreen/Native` | Windows API declarations |
| `src/WinScreen/Windows` | Window changes, crop, restoration, crash recovery |
| `src/WinScreen/Hotkeys` | Global hotkeys, single instance, clean shutdown requests |
| `src/WinScreen/Pip` | DWM thumbnails, PiP interaction, live shrink tiles |
| `src/WinScreen/Dark` | Dark overlays |
| `src/WinScreen/Anchors` | Floating shortcut buttons and their lifetime |
| `src/WinScreen/Settings` | Saved preferences, localization, settings UI |
| `src/WinScreen/Tray` | Tray commands, toolbar, startup registration |
| `src/WinScreen/Updates` | Signed source, constrained transport, update lifecycle |
| `tests/WinScreen.Tests` | Logic, UI, native window, security, and app tests |
| `tests/WinScreen.TestTarget` | Disposable external window used by tests |
| `tools/WinScreen.UpdateSigning` | Feed-signing helper; contains no private key |
| `docs` | User/developer guides and publication manifest |

PiP uses DWM thumbnails rather than a pixel-copy capture loop. A second thumbnail shows crop selection because
nothing can be painted above a thumbnail in the same window. Pinning first raises the target and reads back the
topmost state because Windows may report success without actually pinning a background window.

## Known limits and validation gaps

- Direct text selection by dragging in PiP and customizable toolbar styles are not implemented.
- Tray-menu interaction and file-drag activation over an Anchor need broader end-to-end coverage.
- Red pin borders combined with dark mode, and cropping combined with dark mode, need further testing.
- Cropping has mainly been tested on a plain test window; apps may redraw their window region.
- Real Windows sign-in/startup registration and its removal on uninstall have not been verified end to end.
- A real two-version installed upgrade and a live signed update from hosted releases still need verification.
- Elevated target apps may reject control. Hidden browsers may suspend rendering and freeze PiP.
- Testing has mainly used one 1920×1080 display at 100% scaling on Windows 11 26H2. More monitor/DPI/Windows 10 testing is needed.
- See the repository's Actions page for current CI results; local checks do not establish that hosted workflows pass.

Source commits exclude generated builds, installers, private keys, IDE settings, runtime preferences, and local development records.
The exact intended source list is in [PUBLIC_FILES.md](PUBLIC_FILES.md). Installer packages belong in GitHub Releases.
