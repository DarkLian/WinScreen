# Development and releases

[README](../README.md) · [User Guide](USER_GUIDE.md) · [Security](../SECURITY.md)

## Build

Use the .NET 10 SDK pinned in `global.json` on Windows. `build.ps1` prefers the SDK under
`%LOCALAPPDATA%\Microsoft\dotnet` if installed there.

```powershell
.\build.ps1 -SkipInteractive
```

This builds the solution, runs logic tests without desktop input, publishes a self-contained app, and creates
`dist\release\WinScreen-win-Setup.exe`. Each run replaces the generated `dist\win-x64` and `dist\release` directories.

| Command | Additional behavior |
| --- | --- |
| `.\build.ps1` | Also runs the packaged app end to end; briefly uses the mouse and keyboard |
| `.\build.ps1 -SkipInteractive` | Excludes native desktop and packaged-app interactive tests |
| `.\build.ps1 -DesktopTests -SkipInteractive` | Runs native desktop tests; may move the pointer or type into disposable windows |
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
Keep the private key outside the repository and back it up securely. CI reads the repository secret. A mismatched key is rejected.

Push a stable version tag such as **v0.4.3**, using a higher version for each release. The Release workflow:

1. Builds with the tag's version and embeds the hosting repository.
2. Packages and Authenticode-signs the application/installer, verifies its expected publisher, and signs the exact `releases.win.json` bytes using RSA-PSS/SHA-256.
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

Signing update metadata is separate from Windows Authenticode signing. Local developer installers remain unsigned unless signing is configured. Public publication rejects unsigned installers.
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
| `docs` | User and developer guides |

PiP uses DWM thumbnails rather than a pixel-copy capture loop. A second thumbnail shows crop selection because
nothing can be painted above a thumbnail in the same window. Pinning first raises the target and reads back the
topmost state because Windows may report success without actually pinning a background window. PiP views and Shrink
tiles avoid that by being created topmost, and they use `WS_EX_NOACTIVATE`; answering `WM_MOUSEACTIVATE` alone left
the foreground window empty after a repeat press.

Thumbnail coordinates start at the `GetWindowRect` corner, but `DwmQueryThumbnailSourceSize` reports only the frame's
size, so the visible part reaches past it. Clipping to the reported size loses the right and bottom edges. Hidden
originals stay topmost, outside the pinned state, because covered Chromium windows stop drawing. Test framed windows:
the borderless test target has no invisible margins.

## Known limits and validation gaps

- Direct text selection by dragging in PiP and customizable toolbar styles are not implemented.
- Tray-menu interaction and file-drag activation over an Anchor need broader end-to-end coverage.
- Red pin borders combined with dark mode, and cropping combined with dark mode, need further testing.
- Cropping has mainly been tested on a plain test window; apps may redraw their window region.
- Real Windows sign-in/startup registration and its removal on uninstall have not been verified end to end.
- A real two-version installed upgrade and a live signed update from hosted releases still need verification.
- Elevated target apps may reject control. Covered, minimized, or otherwise occluded browsers may suspend rendering and freeze PiP.
- PiP across virtual desktops and monitors with different scaling has not been tested.
- Testing has mainly used one 1920×1080 display at 100% scaling on Windows 11 Pro build 26300. More monitor/DPI/Windows 10 testing is needed.
- See the repository's Actions page for current CI results; local checks do not establish that hosted workflows pass.

Installer packages belong in GitHub Releases.

## Audit remediation and release gates (0.5)

Public release also requires a successful manual Desktop validation workflow for the exact commit, a Windows signing
provider configured through the protected secret `WINSCREEN_AUTHENTICODE_SIGN_PARAMS`, and the expected publisher
thumbprint in `WINSCREEN_PUBLISHER_THUMBPRINT`. Provision that signing provider/certificate in the release job before
packaging. The workflow does not invent or purchase a trusted signing certificate.
Protect the `release` environment with required reviewers and restrict stable version tags. Action revisions are pinned.

Run the `interactive-desktop` self-hosted runner only on a dedicated, disposable, signed-in Windows VM. Do not attach a
personal workstation runner to this public repository. A fast CI pass does not replace native/interactive validation.
Installer install, startup at sign-in, update N to N+1, uninstall, and the mixed-DPI/monitor matrix still need real VM runs.
See [audit implementation and remaining acceptance checks](AUDIT_IMPLEMENTATION.md) for the finding-by-finding status.
