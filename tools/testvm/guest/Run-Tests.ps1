<#
  Runs a test bundle in the signed-in desktop and writes results next to it.

  Inside the test VM, the WinScreenTests scheduled task starts this with no arguments.
  It also runs anywhere else for a dry run, for example on a build machine:
      .\Run-Tests.ps1 -Root <bundle folder> -Dotnet <path to dotnet.exe>

  The bundle folder holds run.json, tests\ (the built test output) and, for the packaged suite,
  program\ (the published WinScreen). Results go to <Root>\out: one .trx per step, runner.log,
  environment.json, <step>.returned-early.txt for tests that returned early (Skipped.Because), and exitcode.txt,
  which is written last.
#>
param(
    [string]$Root = 'C:\WinScreenTest\run',
    [string]$Dotnet = (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $Root 'out'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exitFile = Join-Path $out 'exitcode.txt'
Remove-Item $exitFile -ErrorAction SilentlyContinue
$code = 1
Start-Transcript -Path (Join-Path $out 'runner.log') -Force | Out-Null
try {
    $config = Get-Content -Raw -LiteralPath (Join-Path $Root 'run.json') | ConvertFrom-Json

    if (@(Get-Process LogonUI -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'The desktop is locked or at the sign-in screen, so desktop tests cannot run.'
    }
    if ([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or -not [Environment]::UserInteractive) {
        throw 'Not running in a signed-in desktop session.'
    }

    Add-Type -AssemblyName System.Windows.Forms
    $os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    [ordered]@{
        computer = $env:COMPUTERNAME
        # ProductName says "Windows 10" on Windows 11 too; the build number tells them apart.
        windows = $(if ([int]$os.CurrentBuildNumber -ge 22000) { '11' } else { '10' })
        product = $os.ProductName
        displayVersion = $os.DisplayVersion
        build = "$($os.CurrentBuildNumber).$($os.UBR)"
        architecture = $env:PROCESSOR_ARCHITECTURE
        screens = @([Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.Bounds.Width)x$($_.Bounds.Height)" })
        gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object Name)
        started = (Get-Date).ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'environment.json') -Encoding UTF8

    $tests = Join-Path $Root 'tests\WinScreen.Tests.dll'
    if (-not (Test-Path -LiteralPath $tests)) { throw "Test assembly not found: $tests" }

    $code = 0
    foreach ($step in $config.steps) {
        $saved = @{}
        if ($step.PSObject.Properties['env'] -and $step.env) {
            foreach ($pair in $step.env.PSObject.Properties) {
                $value = ([string]$pair.Value).Replace('{root}', $Root)
                $saved[$pair.Name] = [Environment]::GetEnvironmentVariable($pair.Name)
                [Environment]::SetEnvironmentVariable($pair.Name, $value)
            }
        }
        # Tests that return early say so here (Skipped.Because); the .trx counts them as passed.
        $saved['WINSCREEN_SKIPPED_FILE'] = $env:WINSCREEN_SKIPPED_FILE
        $env:WINSCREEN_SKIPPED_FILE = Join-Path $out "$($step.name).returned-early.txt"
        Remove-Item -LiteralPath $env:WINSCREEN_SKIPPED_FILE -ErrorAction SilentlyContinue
        try {
            Write-Output "== $($step.name): $($step.filter)"
            # A hung test is stopped and named in the results instead of blocking the run.
            $hang = if ($config.PSObject.Properties['hangMinutes'] -and $config.hangMinutes) { [int]$config.hangMinutes } else { 3 }
            # Windows PowerShell turns a native program's error output into errors, so don't stop on them here.
            $ErrorActionPreference = 'Continue'
            & $Dotnet test $tests --filter $step.filter --logger "trx;LogFileName=$($step.name).trx" `
                --logger 'console;verbosity=normal' --results-directory $out `
                --blame-hang --blame-hang-timeout "$($hang)m" --blame-hang-dump-type none 2>&1 | ForEach-Object { "$_" }
            $stepCode = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            Write-Output "== $($step.name) finished with exit code $stepCode"
            if ($stepCode -ne 0 -and $code -eq 0) { $code = $stepCode }
        }
        finally {
            foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }
    }
}
catch {
    Write-Output "Runner failed: $_"
    if ($code -eq 0) { $code = 1 }
}
finally {
    Stop-Transcript | Out-Null
    # Written last: the host waits for this file before it copies the results.
    Set-Content -LiteralPath $exitFile -Value $code -Encoding ASCII
}
exit $code
