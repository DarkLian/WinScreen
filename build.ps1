<#
  Builds, tests and packages WinScreen.

    .\build.ps1                     Everything, on a real Windows desktop. The final step runs the
                                    packaged WinScreen.exe and takes over the mouse and keyboard
                                    for a few seconds.
    .\build.ps1 -SkipInteractive    Logic tests and packaging only; no desktop input.
    .\build.ps1 -DesktopTests -SkipInteractive
                                    Includes native desktop tests; may use the mouse/keyboard.
    .\build.ps1 -CI                 For build servers with no desktop: logic tests only.
    .\build.ps1 -Beta 4             A numbered local beta: version 0.8.0-beta.4 (the version in Directory.Build.props
                                    plus .4), kept in dist\local\0.8.0-beta.4\<arch>. Never published.
    .\build.ps1 -Release -Version 0.8.0-beta
                                    A published release, as the Release workflow builds it from the tag v0.8.0-beta.
    .\build.ps1 -Version 1.2.3      Use this version instead of the one in Directory.Build.props.
    .\build.ps1 -Architecture arm64 Native ARM64. Tests run only on a machine that can run them;
                                    built elsewhere, it compiles and packages without testing.
    .\build.ps1 -Stage Publish      Stops once the program is in dist\win-<arch>, so its files can be
                                    code-signed before they go into the installer.
    .\build.ps1 -Stage Pack         Makes the installer from what's already in dist\win-<arch>.

  Every build is local unless -Release is given. A local build goes to dist\local\<version>\<arch>, and its installer
  and portable zip are named with the version, the release type and the architecture, for example
  WinScreen-0.8.0-beta.4-local-x64-Setup.exe. Its program's file description says "WinScreen (local build)".
  A release goes to dist\release (x64) or dist\release-arm64 with Velopack's own names (WinScreen-win-Setup.exe), which
  code signing expects; the Release workflow renames them to WinScreen-<version>-release-<arch>-Setup.exe after signing.
  Each architecture has its own update channel, so an installed copy can't take the other's update.
#>
param(
    [switch]$SkipInteractive,
    [switch]$CI,
    [switch]$DesktopTests,
    [string]$Version,
    [ValidateRange(1, 999)][int]$Beta,
    [switch]$Release,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$UpdateRepository = 'DarkLian/WinScreen',
    [switch]$SignUpdates,
    [ValidateSet('All', 'Publish', 'Pack')][string]$Stage = 'All'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }

if ($UpdateRepository -and ($UpdateRepository -notmatch '\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z' -or $UpdateRepository.Split('/')[1] -in '.', '..')) {
    throw 'UpdateRepository must be a GitHub owner/repository name.'
}
$updateProperty = "-p:UpdateRepository=$UpdateRepository"
if ($UpdateRepository -and -not (Test-Path -LiteralPath 'src\WinScreen\Updates\update-public-key.pem')) {
    throw 'An embedded update verification public key is required.'
}

function Step([string]$title, [string[]]$arguments) {
    Write-Output "== $title"
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$title failed." }
}

if ($Release -and -not $Version) { throw 'A release takes its version from its tag: pass -Version.' }
if ($Release -and $Beta) { throw '-Beta is for local builds; a release is numbered by its tag.' }
if ($Beta -and $Version) { throw 'Give either -Beta or -Version.' }
if (-not $Version) {
    $Version = ([xml](Get-Content 'Directory.Build.props')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($Beta) {
        if ($Version -notmatch '-(alpha|beta|rc)\z') { throw "Local betas number a prerelease version; $Version isn't one." }
        $Version = "$Version.$Beta"
    }
}
$releaseType = if ($Release) { 'release' } else { 'local' }
$releaseTypeProperty = "-p:WinScreenReleaseType=$releaseType"

$runtime = "win-$Architecture"
$channel = if ($Architecture -eq 'x64') { 'win' } else { $runtime }
$architectureProperty = "-p:WinScreenArchitecture=$Architecture"
$canTest = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant() -eq $Architecture
$program = Join-Path $PSScriptRoot "dist\$runtime"
$packageFolder = Join-Path $PSScriptRoot $(if (-not $Release) { "dist\local\$Version\$Architecture" } elseif ($Architecture -eq 'x64') { 'dist\release' } else { "dist\release-$Architecture" })
if ($Beta -and $Stage -ne 'Pack' -and (Test-Path (Join-Path $packageFolder '*-Setup.exe'))) {
    throw "$Version is already built in $packageFolder. Local betas aren't rebuilt: use the next number."
}
foreach ($folder in $program, $packageFolder) {
    $resolvedOutput = [IO.Path]::GetFullPath($folder)
    if (-not $resolvedOutput.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build output is outside the workspace: $resolvedOutput"
    }
    # Packing on its own uses the program that's already there, which may have been signed since.
    if ((Test-Path $folder) -and -not ($Stage -eq 'Pack' -and $folder -eq $program)) { Remove-Item $folder -Recurse -Force }
}
if ($Stage -eq 'Pack' -and -not (Test-Path (Join-Path $program 'WinScreen.exe'))) {
    throw "Nothing to pack in $program. Run with -Stage Publish first."
}
if ($Stage -eq 'Pack') {
    $built = (Get-Item (Join-Path $program 'WinScreen.exe')).VersionInfo.ProductVersion
    if ($built -ne $Version) { throw "$program has version $built, not $Version. Run with -Stage Publish first." }
}

if ($Stage -ne 'Pack') {
Step 'Build'@('build', 'WinScreen.slnx', '-c', 'Release', "-p:Version=$Version", $updateProperty, $architectureProperty, $releaseTypeProperty)

$filter = if ($CI -or ($SkipInteractive -and -not $DesktopTests)) { 'Category!=RealWindows&Category!=Interactive' } else { 'Category!=Interactive' }
if ($canTest) {
    Step 'Test' @('test', 'WinScreen.slnx', '-c', 'Release', '--no-build', '--filter', $filter, $architectureProperty)
}
else {
    Write-Output "== Test skipped: this machine can't run $Architecture code."
}

# Self-contained, so people don't have to install .NET first.
Step 'Package' @('publish', 'src\WinScreen\WinScreen.csproj', '-c', 'Release', '-o', $program,
    '--self-contained', 'true', '-p:DebugType=embedded', "-p:Version=$Version", $updateProperty, $architectureProperty, $releaseTypeProperty)

if ($canTest -and -not $CI -and -not $SkipInteractive) {
    $env:WINSCREEN_EXE = Join-Path $program 'WinScreen.exe'
    Step 'Test the packaged program' @('test', 'WinScreen.slnx', '-c', 'Release', '--no-build', '--filter', 'Category=Interactive', $architectureProperty)
    Remove-Item Env:\WINSCREEN_EXE
}
}

if ($Stage -eq 'Publish') {
    Write-Output "Done: $program"
    return
}

Step 'Restore packaging tool'@('tool', 'restore')
$packageArguments = @('tool', 'run', 'vpk', '--', 'pack',
    '--packId', 'WinScreen', '--packVersion', $Version, '--packDir', $program, '--mainExe', 'WinScreen.exe',
    '--runtime', $runtime, '--channel', $channel, '--packTitle', 'WinScreen', '--packAuthors', 'WinScreen', '--icon', 'src\WinScreen\Assets\WinScreen.ico',
    '--outputDir', $packageFolder)
if ($env:WINSCREEN_AUTHENTICODE_SIGN_PARAMS) {
    $packageArguments += @('--signParams', $env:WINSCREEN_AUTHENTICODE_SIGN_PARAMS)
}
Step 'Make the installer' $packageArguments

$installer = Join-Path $packageFolder "WinScreen-$channel-Setup.exe"
if (-not $Release) {
    # A local build carries its version and type in its file names, so copies of different builds can't be confused.
    $name = "WinScreen-$Version-$releaseType-$Architecture"
    Rename-Item $installer "$name-Setup.exe"
    Rename-Item (Join-Path $packageFolder "WinScreen-$channel-Portable.zip") "$name-Portable.zip"
    $installer = Join-Path $packageFolder "$name-Setup.exe"
}
Write-Output "Done: $installer"

if ($SignUpdates) {
    Step 'Sign update feed' @('run', '--project', 'tools\WinScreen.UpdateSigning\WinScreen.UpdateSigning.csproj', '-c', 'Release', '--',
        'sign', (Join-Path $packageFolder "releases.$channel.json"), (Join-Path $PSScriptRoot 'src\WinScreen\Updates\update-public-key.pem'))
}
