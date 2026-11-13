<#
.SYNOPSIS
  Runs the logic tests (no windows, no mouse or keyboard) and records which commit they tested.

.DESCRIPTION
  Safe while someone uses the PC. Builds (unless -SkipBuild), then runs every test that is neither
  Category=RealWindows nor Category=Interactive.

  Results go to TestResults\logic\<time>: logic.trx, source.json (the commit the build came from, for
  Confirm-Release.ps1), not-run.txt and exitcode.txt. Exit code 0 when every test passed.

.EXAMPLE
  .\tools\Invoke-LogicRun.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'TestRuns.ps1')

$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $env:WINSCREEN_ZIG) { $env:WINSCREEN_ZIG = Join-Path $repo '.local\hover-tools\zig-windows-x86_64-0.13.0\zig.exe' }
$tests = Join-Path $repo 'tests\WinScreen.Tests\bin\Release\net10.0-windows10.0.26100.0\win-x64'

if (-not $SkipBuild) {
    $before = Get-SourceState $repo
    Write-Output '== Building'
    & $dotnet build (Join-Path $repo 'WinScreen.slnx') -c Release -p:WinScreenArchitecture=x64 -p:UpdateRepository=DarkLian/WinScreen -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    Save-BuildStamp $tests $before
}
if (-not (Test-Path (Join-Path $tests 'WinScreen.Tests.dll'))) { throw 'No built tests. Run without -SkipBuild.' }

if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repo ('TestResults\logic\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$ResultsDirectory = (Resolve-Path $ResultsDirectory).Path

$source = Get-RunSource $repo @(Join-Path $tests 'WinScreen.Tests.dll')
Save-RunRecord $ResultsDirectory $source @{ tool = 'logic' }
if (-not $source.commit -or -not $source.clean) { Write-Output "Not a release run: $($source.note)" }

$saved = $env:WINSCREEN_SKIPPED_FILE
$env:WINSCREEN_SKIPPED_FILE = Join-Path $ResultsDirectory 'logic.returned-early.txt'
try {
    Write-Output '== Running the logic tests'
    $ErrorActionPreference = 'Continue'
    & $dotnet test (Join-Path $tests 'WinScreen.Tests.dll') --filter 'Category!=RealWindows&Category!=Interactive' `
        --logger 'trx;LogFileName=logic.trx' --results-directory $ResultsDirectory 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
}
finally { $env:WINSCREEN_SKIPPED_FILE = $saved }

$outcomes = @(Get-RunOutcomes $ResultsDirectory)
$outcomes | ForEach-Object { Write-Output (Format-Outcome $_) }
$notRun = @(Save-NotRun $ResultsDirectory $outcomes)
$notRun | ForEach-Object { Write-Output "- not run: $_" }
Set-Content -LiteralPath (Join-Path $ResultsDirectory 'exitcode.txt') -Value $code -Encoding ASCII
Write-Output "Results: $ResultsDirectory (exit code $code)"
exit $code
