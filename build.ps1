<#
  Builds, tests and packages WinScreen.

    .\build.ps1                     Everything, on a real Windows desktop. The final step runs the
                                    packaged WinScreen.exe and takes over the mouse and keyboard
                                    for a few seconds.
    .\build.ps1 -SkipInteractive    Everything except that final step.
    .\build.ps1 -CI                 For build servers with no desktop: logic tests only.
    .\build.ps1 -Version 1.2.3      Use this version instead of the one in Directory.Build.props.

  The installer is dist\release\WinScreen-win-Setup.exe.
#>
param(
    [switch]$SkipInteractive,
    [switch]$CI,
    [string]$Version,
    [string]$UpdateRepository = 'DarkLian/WinScreen',
    [switch]$SignUpdates
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

if (-not $Version) {
    $Version = ([xml](Get-Content 'Directory.Build.props')).Project.PropertyGroup.Version
}

$program = Join-Path $PSScriptRoot 'dist\win-x64'
$release = Join-Path $PSScriptRoot 'dist\release'
foreach ($folder in $program, $release) {
    $resolvedOutput = [IO.Path]::GetFullPath($folder)
    if (-not $resolvedOutput.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build output is outside the workspace: $resolvedOutput"
    }
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}

Step 'Build' @('build', 'WinScreen.slnx', '-c', 'Release', "-p:Version=$Version", $updateProperty)

$filter = if ($CI) { 'Category!=RealWindows&Category!=Interactive' } else { 'Category!=Interactive' }
Step 'Test' @('test', 'WinScreen.slnx', '-c', 'Release', '--no-build', '--filter', $filter)

# Self-contained, so people don't have to install .NET first.
Step 'Package' @('publish', 'src\WinScreen\WinScreen.csproj', '-c', 'Release', '-o', $program,
    '--self-contained', 'true', '-p:DebugType=embedded', "-p:Version=$Version", $updateProperty)

if (-not $CI -and -not $SkipInteractive) {
    $env:WINSCREEN_EXE = Join-Path $program 'WinScreen.exe'
    Step 'Test the packaged program' @('test', 'WinScreen.slnx', '-c', 'Release', '--no-build', '--filter', 'Category=Interactive')
    Remove-Item Env:\WINSCREEN_EXE
}

Step 'Restore packaging tool' @('tool', 'restore')
Step 'Make the installer' @('tool', 'run', 'vpk', '--', 'pack',
    '--packId', 'WinScreen', '--packVersion', $Version, '--packDir', $program, '--mainExe', 'WinScreen.exe',
    '--runtime', 'win-x64', '--packTitle', 'WinScreen', '--packAuthors', 'WinScreen', '--icon', 'src\WinScreen\Assets\WinScreen.ico',
    '--outputDir', $release)

Write-Output "Done: $(Join-Path $release 'WinScreen-win-Setup.exe')"

if ($SignUpdates) {
    Step 'Sign update feed' @('run', '--project', 'tools\WinScreen.UpdateSigning\WinScreen.UpdateSigning.csproj', '-c', 'Release', '--',
        'sign', (Join-Path $release 'releases.win.json'), (Join-Path $PSScriptRoot 'src\WinScreen\Updates\update-public-key.pem'))
}
