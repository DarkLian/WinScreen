# Update security

The updater uses Velopack 1.2.161 for installed-app detection, version selection, download caching and installation.
WinScreen adds a signed update source and an opt-in lifecycle around it.

## Checks before installation

- The GitHub `owner/repository` is embedded in assembly metadata at build time. Settings cannot redirect updates.
- Downloads require HTTPS with normal certificate validation. Every redirect is checked before requesting it.
  Only this repository's GitHub release paths and GitHub's `release-assets.githubusercontent.com` and
  `objects.githubusercontent.com` download hosts are allowed. Credentials, nonstandard ports, local paths and other
  hosts are rejected. No GitHub tokens are embedded or sent.
- `releases.win.json.sig` must be an RSA-PSS/SHA-256 signature of the exact feed bytes, verified using the embedded
  public key (RSA 3072 bits or stronger). The feed is not parsed until verification succeeds. Signing uses .NET's
  standard cryptography implementation. Unsigned feeds have no fallback.
- Latest is resolved once to an exact stable release tag before requesting metadata. Feed, signature and package use
  that versioned release path; signed versions must match the tag, avoiding separate latest-download requests during rollover.
- Only stable full packages for `WinScreen` on the `win` channel are accepted. Package paths/URLs, missing SHA-256,
  invalid sizes, deltas and prereleases are rejected. Only versions newer than the current installation are selected.
- Metadata is limited to 1 MiB, signatures to 1 KiB, and packages to 512 MiB. Downloads have a ten-minute deadline
  and at most five redirects. Streaming limits also apply when Content-Length is absent or incorrect.
- Full packages must match the signed size and SHA-256 after download and again immediately before scheduling
  installation. Partial or rejected source downloads are deleted. Remote release notes are not rendered or executed.
- Velopack's automatic startup application of cached packages is disabled. Only a verified package from the current
  enabled session may be applied, after normal window restoration on exit. Disabling automatic updates cancels
  checks/downloads and discards the session's pending application. There is no forced restart.

## Trust and limits

The private update-signing key is outside the repository and restricted to the generating Windows user by an explicit
Windows ACL. Only its public key ships in the app. CI requires the matching private key in the repository's protected
Actions secret. A compromised release host alone cannot forge a new signed feed; compromise of the signing key or
the release build/signing workflow can authorize malicious updates. Protect the repository, release workflow and key.

The installer and executable are still **not Authenticode signed**. Signed update metadata authenticates subsequent
packages to an already trusted installation; it does not authenticate a first downloaded installer to Windows or
remove SmartScreen warnings. Verify initial distribution independently. This is not a claim that the app is malware-free.

Public release workflows now require a valid installer signature from the configured publisher and successful desktop
validation for the exact commit. The certificate/provider, expected thumbprint, dedicated desktop runner and protected
release environment still need external configuration. Local developer builds remain unsigned unless signing is configured.

Version checks prevent installing an older/equal version, but there is no signed-feed expiry or independent transparency
log: a host can withhold updates. An attacker who can already change files as the current Windows user can tamper
with that user's app, settings and private key; this is outside this updater's trust boundary.

## Verification performed

Automated tests exercise signature tampering, wrong keys, missing signatures, invalid identity/channel metadata,
path/URL injection, prereleases, deltas, missing strong hashes, download limits, unsafe redirects, modified/truncated
cached packages, disabling before and during download, concurrent checks, and failure/retry behavior.
The NuGet vulnerability audit includes transitive dependencies; absence of known advisories is not proof of safety.

A live two-version upgrade from the chosen public release repository must still be tested after hosting and the CI
signing secret are configured. Keep the signed feed, signature and full package in each GitHub release.
