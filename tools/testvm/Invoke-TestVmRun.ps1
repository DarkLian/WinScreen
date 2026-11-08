<#
.SYNOPSIS
  Runs the desktop tests inside a test VM, starting from and returning to its clean checkpoint.

.DESCRIPTION
  1. Builds the tests on this PC (and publishes WinScreen for the packaged suite).
  2. Turns the VM off and restores the checkpoint, so every run starts from the same signed-in desktop.
  3. Copies the build into the VM and starts the WinScreenTests task, which runs the tests in the
     VM's own desktop. Mouse and keyboard input stay inside the VM.
  4. Copies the results back, then turns the VM off and restores the checkpoint again.

  Results go to TestResults\vm\<Name>-<time> by default: a .trx per suite, runner.log and
  environment.json (Windows build, screen size, graphics). The exit code is 0 when every suite
  passed, so the script can run as-is from a CI job on this PC.

  The suites follow build.ps1: "Desktop" is everything except Category=Interactive, and "Packaged"
  is Category=Interactive against the published program.

  Don't keep a Hyper-V enhanced session open to the VM during a run: it can take over the desktop
  the tests use. A basic session window is fine for watching.

  The VM has no graphics card of its own, so effects that need hardware graphics are expected to
  refuse to start there.

.EXAMPLE
  .\Invoke-TestVmRun.ps1

.EXAMPLE
  .\Invoke-TestVmRun.ps1 -Suite Desktop -Filter "FullyQualifiedName~Anchor" -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$Name = 'WinScreen-Win10',
    [string]$CheckpointName = 'clean',
    [ValidateSet('All', 'Desktop', 'Packaged')][string]$Suite = 'All',
    # Runs only matching tests, for example "FullyQualifiedName~Anchor". Narrows the Packaged suite too.
    [string]$Filter,
    [switch]$SkipBuild,
    [string]$ResultsDirectory,
    [int]$TimeoutMinutes = 60,
    # Leave the VM running after the run, to look around. The next run still restores the checkpoint.
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestVm.Common.ps1')
Assert-HyperVAccess

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$state = Get-TestVmStateFolder $Name
$credential = Get-TestVmCredential $Name
if (-not (Get-VM -Name $Name -ErrorAction SilentlyContinue)) { throw "No VM named '$Name'. Create it with New-TestVm.ps1." }
if (-not (Get-VMSnapshot -VMName $Name -Name $CheckpointName -ErrorAction SilentlyContinue)) {
    throw "VM '$Name' has no checkpoint '$CheckpointName'."
}

$info = Join-Path $state 'vm.json'
if (Test-Path $info) {
    $vmInfo = Get-Content -Raw $info | ConvertFrom-Json
    $age = ((Get-Date) - [DateTime]$vmInfo.created).Days
    if ($vmInfo.edition -match 'Eval' -and $age -ge $script:EvaluationWarningDays) {
        Write-Warning "This VM's evaluation copy of Windows is $age days old and stops working at 90. Rebuild it with New-TestVm.ps1 -Force."
    }
}

if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $repo ("TestResults\vm\$Name-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$ResultsDirectory = (Resolve-Path $ResultsDirectory).Path

$lock = [Threading.Mutex]::new($false, "Global\WinScreen.TestVm.$Name")
$owned = $false
try { $owned = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) { throw "Another run is using '$Name'." }

function Step([string]$text) { Write-Output "== $text" }
function Dotnet([string[]]$arguments) {
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) failed." }
}

$exitCode = 1
try {
    $dotnet = Join-Path (Find-HostDotnet) 'dotnet.exe'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $staging = Join-Path $repo 'TestResults\vm\_bundle'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging | Out-Null
    $common = @('-c', 'Release', '-p:WinScreenArchitecture=x64', '-p:UpdateRepository=DarkLian/WinScreen')

    if (-not $SkipBuild) {
        Step 'Building'
        Dotnet (@('build', (Join-Path $repo 'WinScreen.slnx')) + $common)
    }
    $testOutput = Get-ChildItem (Join-Path $repo 'tests\WinScreen.Tests\bin\Release') -Directory |
        Where-Object Name -like 'net10.0-windows10.0.26100.0' |
        ForEach-Object { Join-Path $_.FullName 'win-x64' } | Where-Object { Test-Path (Join-Path $_ 'WinScreen.Tests.dll') } |
        Select-Object -First 1
    if (-not $testOutput) { throw 'No built tests found. Run without -SkipBuild.' }
    Copy-Item $testOutput (Join-Path $staging 'tests') -Recurse

    $steps = @()
    if ($Suite -in 'All', 'Desktop') {
        $steps += [ordered]@{ name = 'desktop'; filter = $(if ($Filter) { $Filter } else { 'Category!=Interactive' }) }
    }
    if ($Suite -in 'All', 'Packaged') {
        Step 'Publishing the packaged program'
        Dotnet (@('publish', (Join-Path $repo 'src\WinScreen\WinScreen.csproj'), '-o', (Join-Path $staging 'program'),
            '--self-contained', 'true', '-p:DebugType=embedded') + $common)
        $packagedFilter = if ($Filter) { "Category=Interactive&($Filter)" } else { 'Category=Interactive' }
        $steps += [ordered]@{ name = 'packaged'; filter = $packagedFilter; env = @{ WINSCREEN_EXE = '{root}\program\WinScreen.exe' } }
    }
    [ordered]@{ steps = $steps } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $staging 'run.json') -Encoding UTF8
    Copy-Item (Join-Path $PSScriptRoot 'guest') (Join-Path $staging 'guest') -Recurse
    $bundle = Join-Path $repo 'TestResults\vm\bundle.zip'
    Remove-Item $bundle -ErrorAction SilentlyContinue
    & tar.exe -a -c -f $bundle -C $staging .
    if ($LASTEXITCODE -ne 0) { throw 'Packing the bundle failed.' }

    Step "Restoring '$Name' to '$CheckpointName'"
    Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
    Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false
    Start-VM -Name $Name
    $session = Wait-TestVmSession -Name $Name -Credential $credential -TimeoutSeconds 600
    try {
        Wait-TestVmDesktop -Session $session -TimeoutSeconds 300
        Step 'Copying the build in'
        $guestRoot = $script:GuestRoot
        Invoke-Command -Session $session -ScriptBlock {
            param($root)
            Remove-Item "$root\run" -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Path "$root\run" -Force | Out-Null
        } -ArgumentList $guestRoot
        Copy-Item -LiteralPath $bundle -Destination "$guestRoot\run\bundle.zip" -ToSession $session
        Invoke-Command -Session $session -ScriptBlock {
            param($root, $task)
            $ErrorActionPreference = 'Stop'
            $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
            if (-not (Test-Path $tar)) { throw "VM: $tar is missing." }
            $unpacked = & $tar -x -f "$root\run\bundle.zip" -C "$root\run" 2>&1
            if ($LASTEXITCODE -ne 0) { throw "VM: unpacking the build failed: $unpacked" }
            Remove-Item "$root\run\bundle.zip"
            # The runner from this checkout, so script changes don't need a new VM.
            New-Item -ItemType Directory -Path "$root\guest" -Force | Out-Null
            Copy-Item "$root\run\guest\*" "$root\guest" -Recurse -Force
            if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) {
                throw "VM: the scheduled task '$task' doesn't exist. Rebuild the VM with New-TestVm.ps1 -Force."
            }
            Start-ScheduledTask -TaskName $task
        } -ArgumentList $guestRoot, $script:GuestTaskName

        Step "Running tests in the VM (up to $TimeoutMinutes minutes)"
        Wait-Until -What 'the tests to finish' -TimeoutSeconds ($TimeoutMinutes * 60) -PollSeconds 15 -Condition {
            Invoke-Command -Session $session -ScriptBlock { param($root) Test-Path "$root\run\out\exitcode.txt" } -ArgumentList $guestRoot
        } | Out-Null

        Step 'Copying results back'
        Copy-Item -Path "$guestRoot\run\out\*" -Destination $ResultsDirectory -Recurse -Force -FromSession $session
        $exitCode = [int](Get-Content (Join-Path $ResultsDirectory 'exitcode.txt') -Raw).Trim()
    }
    finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
}
catch {
    Write-Output "Run failed: $_"
    Write-Output $_.InvocationInfo.PositionMessage
    Write-Output $_.ScriptStackTrace
    $exitCode = if ($exitCode -eq 0) { 1 } else { $exitCode }
}
finally {
    if (-not $KeepRunning) {
        Step "Returning '$Name' to '$CheckpointName'"
        Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
        Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false -ErrorAction SilentlyContinue
    }
    $lock.ReleaseMutex()
    $lock.Dispose()
}

foreach ($trx in Get-ChildItem $ResultsDirectory -Filter *.trx -ErrorAction SilentlyContinue) {
    $counters = ([xml](Get-Content $trx.FullName -Raw)).TestRun.ResultSummary.Counters
    Write-Output ("{0}: {1} passed, {2} failed, {3} not run" -f $trx.BaseName, $counters.passed, $counters.failed, $counters.notExecuted)
}
Write-Output "Results: $ResultsDirectory (exit code $exitCode)"
exit $exitCode
