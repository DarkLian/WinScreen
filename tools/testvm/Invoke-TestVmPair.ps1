<#
.SYNOPSIS
  Runs the whole desktop and packaged suites on two test VMs at once, about half the time of one VM.

.DESCRIPTION
  Builds and publishes once on this PC, then starts Invoke-TestVmRun.ps1 for each VM side by side:
  - the first VM takes the slow browser and hover classes of the Packaged suite;
  - the second takes the Desktop suite and the rest of the Packaged suite.
  The split comes from measured times (13 Nov 2026: about 12 and 13 minutes of tests). Every Packaged test
  runs on exactly one VM: the second VM's filter is the first one's turned around.

  Each VM keeps the memory its checkpoint was taken with (a checkpoint of a running VM can't be given other
  memory), so both together need that much free on this PC, plus some for the PC itself.

  Results go to TestResults\vm\pair-<time>\<VM name>. The exit code is 0 only when both runs passed.

.EXAMPLE
  .\Invoke-TestVmPair.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Names = @('WinScreen-Rework', 'WinScreen-Win10'),
    # Left free for this PC itself while both VMs run.
    [int]$SpareGB = 3,
    [int]$TimeoutMinutes = 60,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestVm.Common.ps1')
Assert-HyperVAccess
if ($Names.Count -ne 2) { throw 'Give exactly two VM names.' }

# The slow classes, by measured time: these alone are about half of all test time.
$heavy = @('BrowserInputTests', 'FrameworkHoverTests', 'ExplorerHoverTests')
$takeHeavy = ($heavy | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
$leaveHeavy = ($heavy | ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'

$freeGB = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
$neededGB = $SpareGB
foreach ($vm in Get-VM -Name $Names) {
    $neededGB += $vm.MemoryStartup / 1GB
    if ($vm.State -eq 'Running') { $freeGB += $vm.MemoryAssigned / 1GB }
}
if ($freeGB -lt $neededGB) {
    throw ("Both VMs and this PC need about {0:N0} GB; {1:N1} GB is free. Close something, or use Invoke-TestVmRun.ps1." -f $neededGB, $freeGB)
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = Join-Path (Find-HostDotnet) 'dotnet.exe'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$zig = Join-Path $repo '.local\hover-tools\zig-windows-x86_64-0.13.0\zig.exe'
if (-not $env:WINSCREEN_ZIG -and (Test-Path $zig)) { $env:WINSCREEN_ZIG = $zig }
$common = @('-c', 'Release', '-p:WinScreenArchitecture=x64', '-p:UpdateRepository=DarkLian/WinScreen')

# Built and published once: two publishes of one project at the same time would share its intermediate files.
$program = Join-Path $repo 'TestResults\vm\_program'
if (-not $SkipBuild) {
    Write-Output '== Building'
    & $dotnet build (Join-Path $repo 'WinScreen.slnx') @common
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}
Write-Output '== Publishing the packaged program'
if (Test-Path $program) { Remove-Item $program -Recurse -Force }
& $dotnet publish (Join-Path $repo 'src\WinScreen\WinScreen.csproj') -o $program --self-contained true '-p:DebugType=embedded' @common
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$root = Join-Path $repo ('TestResults\vm\pair-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$runner = Join-Path $PSScriptRoot 'Invoke-TestVmRun.ps1'
$plans = @(
    @{ Name = $Names[0]; Arguments = @('-Suite', 'Packaged', '-PackagedFilter', $takeHeavy) },
    @{ Name = $Names[1]; Arguments = @('-Suite', 'All', '-PackagedFilter', $leaveHeavy) }
)
$started = Get-Date
$processes = foreach ($plan in $plans) {
    $results = Join-Path $root $plan.Name
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner, '-Name', $plan.Name, '-SkipBuild',
        '-Published', $program, '-TimeoutMinutes', $TimeoutMinutes, '-ResultsDirectory', $results) + $plan.Arguments
    Write-Host "== Starting $($plan.Name): $($plan.Arguments -join ' ')" # Not output: only the processes are collected.
    $process = Start-Process powershell.exe -ArgumentList ($arguments | ForEach-Object { if ($_ -match '[\s&|!]') { '"' + $_ + '"' } else { "$_" } }) `
        -RedirectStandardOutput (Join-Path $root "$($plan.Name).log") -RedirectStandardError (Join-Path $root "$($plan.Name).err.log") `
        -WindowStyle Hidden -PassThru
    $null = $process.Handle # Kept open, or the exit code is gone once the process ends.
    $process
}
$processes | Wait-Process
$exitCode = 0
foreach ($i in 0..1) {
    $log = Join-Path $root "$($plans[$i].Name).log"
    Write-Output "== $($plans[$i].Name)"
    Get-Content $log | Where-Object { $_ -match ' passed, |^Results:|^Run failed' } | ForEach-Object { "   $_" }
    $code = $processes[$i].ExitCode
    if ($code -ne 0) { $exitCode = if ($code) { $code } else { 1 } }
}
Write-Output ("Both done in {0:N0} minutes. Results: {1} (exit code {2})" -f ((Get-Date) - $started).TotalMinutes, $root, $exitCode)
exit $exitCode
