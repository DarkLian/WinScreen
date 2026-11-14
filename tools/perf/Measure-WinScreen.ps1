<#
.SYNOPSIS
    Measures what WinScreen costs while it runs (roadmap P1-4): CPU, context switches, memory and GPU, per scenario.

.DESCRIPTION
    Starts the built WinScreen.exe with its own data folder for each scenario, sets the scenario up through its
    command line (no keys pressed, the pointer is left alone unless -MovePointer), lets it settle, then samples the
    process for -Seconds. Windows to look at are the tests' own: the test target app and the portable Chrome from
    the test output, showing a page that redraws every frame ("video" below; it stands in for a 1080p video and is
    the window's own size, 1280 x 720).

    Results go to TestResults\perf\<time>\results.json with the conditions (Windows build, displays, graphics
    adapter, commit), and a table is printed for docs\PERFORMANCE.md. Run it on the owner's PC with nothing else
    busy; one GPU and one display are what the numbers stand for.

    Metrics come from WMI performance classes, whose names are the same in every Windows language.

.PARAMETER Scenario
    Which scenarios to run; all by default. tray, toolbar, pinned, view, smooth30, smooth60, smooth3, dark, visit.

.PARAMETER MovePointer
    Lets the 'visit' scenario sweep the real pointer over a view used directly. Without it that scenario is skipped.
#>
param(
    [string[]]$Scenario = @('tray', 'toolbar', 'pinned', 'view', 'smooth30', 'smooth60', 'smooth3', 'dark', 'visit'),
    [int]$Seconds = 60,
    [int]$Settle = 8,
    [switch]$MovePointer,
    [string]$ResultsRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ResultsRoot) { $ResultsRoot = Join-Path $root 'TestResults\perf' }
# A self-contained copy, as people get it and as the test runners measure it: it needs no .NET installed.
$publish = Join-Path $root 'artifacts\perf-program'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet publish (Join-Path $root 'src\WinScreen\WinScreen.csproj') -c Release -o $publish --self-contained true -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Publishing WinScreen failed.' }
$program = Join-Path $publish 'WinScreen.exe'
$bin = Join-Path $root 'tests\WinScreen.Tests\bin\Release\net10.0-windows10.0.26100.0\win-x64'
$target = Join-Path $bin 'WinScreen.TestTarget.exe'
$chrome = Join-Path $bin 'chrome-win64\chrome.exe'
# The build output needs the .NET runtime; on a developer PC it may be the SDK's own, not one installed for everyone.
$dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
foreach ($needed in $program, $target) { if (-not (Test-Path $needed)) { throw "Build first: $needed is missing." } }
if (Get-Process WinScreen -ErrorAction SilentlyContinue) { throw 'A WinScreen is running. Close it first; this script never ends it itself.' }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Perf -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
'@

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = New-Item -ItemType Directory -Force (Join-Path $ResultsRoot $stamp)
$work = New-Item -ItemType Directory -Force (Join-Path ([IO.Path]::GetTempPath()) "WinScreenPerf-$stamp")
$started = [Collections.Generic.List[Diagnostics.Process]]::new()
$chromeProfiles = [Collections.Generic.List[string]]::new()

function Write-Settings([hashtable]$extra) {
    $settings = @{ WelcomeShown = $true; ShowToolbar = $false; ShowNotifications = $false; AutomaticUpdates = $false }
    foreach ($key in $extra.Keys) { $settings[$key] = $extra[$key] }
    [IO.File]::WriteAllText((Join-Path $work 'settings.json'), ($settings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}

function Start-WinScreen {
    $start = [Diagnostics.ProcessStartInfo]::new($program)
    $start.UseShellExecute = $false
    $start.Environment['WINSCREEN_DATA'] = $work.FullName
    $start.Environment['DOTNET_ROOT'] = $dotnetRoot
    $process = [Diagnostics.Process]::Start($start)
    $deadline = (Get-Date).AddSeconds(15)
    while ([Perf.Native]::FindWindow([NullString]::Value, 'WinScreen.HotkeyHost') -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'WinScreen did not start.' }
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Seconds 2
    $process
}

function Stop-WinScreen([Diagnostics.Process]$process) {
    # An ordinary close, so it puts windows back first.
    $host_ = [Perf.Native]::FindWindow([NullString]::Value, 'WinScreen.HotkeyHost')
    if ($host_ -ne [IntPtr]::Zero) { [void][Perf.Native]::PostMessage($host_, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    if (-not $process.WaitForExit(8000)) { throw 'WinScreen did not close.' }
}

function Send-WinScreen([string]$command, [string]$window) {
    $start = [Diagnostics.ProcessStartInfo]::new($program)
    $start.UseShellExecute = $false
    # Windows PowerShell runs on .NET Framework, which has no ArgumentList.
    $start.Arguments = "--run $command" + $(if ($window) { " --window `"$window`"" } else { '' })
    $start.Environment['WINSCREEN_DATA'] = $work.FullName
    $start.Environment['DOTNET_ROOT'] = $dotnetRoot
    $run = [Diagnostics.Process]::Start($start)
    if (-not $run.WaitForExit(20000)) { $run.Kill(); throw "WinScreen --run $command didn't answer." }
    if ($run.ExitCode -ne 0) { throw "WinScreen --run $command $window ended with $($run.ExitCode)." }
}

function Start-Target([string]$title, [int]$left = 300, [int]$top = 200) {
    # Windows PowerShell's Start-Process doesn't quote arguments: a title with spaces is quoted here.
    $process = Start-Process -FilePath $target -PassThru -ArgumentList @('--title', "`"$title`"", '--left', $left, '--top', $top, '--width', '640', '--height', '480')
    $started.Add($process)
    Start-Sleep -Seconds 1
}

function Start-Video([string]$title, [int]$left = 100, [int]$top = 100, [switch]$Scrolling) {
    if (-not (Test-Path $chrome)) { throw "The portable Chrome isn't in the test output: $chrome" }
    $page = Join-Path $work "$($title -replace '\W', '-').html"
    $body = if ($Scrolling) {
        "<body style='margin:0;font:22px sans-serif;height:20000px;background:linear-gradient(#fff,#ddd)'>" +
        ((1..400 | ForEach-Object { "<p>Line $_ of text that scrolls past, as a page being read does.</p>" }) -join '') +
        "<script>setInterval(()=>scrollBy(0,2),16)</script>"
    } else {
        "<body style='margin:0;overflow:hidden'><canvas id=c></canvas><script>const c=document.getElementById('c'),x=c.getContext('2d');" +
        "function f(t){c.width=innerWidth;c.height=innerHeight;for(let i=0;i<40;i++){x.fillStyle='hsl('+((t/10+i*9)%360)+',70%,50%)';" +
        "x.fillRect((i*97+t/3)%c.width,(i*53+t/5)%c.height,160,90)}requestAnimationFrame(f)}requestAnimationFrame(f)</script>"
    }
    [IO.File]::WriteAllText($page, "<!doctype html><title>$title</title>$body", [Text.UTF8Encoding]::new($false))
    $profile = Join-Path $work ("chrome-" + [Guid]::NewGuid().ToString('N'))
    $chromeProfiles.Add($profile)
    Start-Process -FilePath $chrome -ArgumentList @("--user-data-dir=$profile", '--no-first-run', '--no-default-browser-check',
        '--disable-background-timer-throttling', '--disable-renderer-backgrounding', "--window-position=$left,$top",
        '--window-size=1280,720', "--app=$([Uri]::new($page).AbsoluteUri)") | Out-Null
    Start-Sleep -Seconds 4
}

function Stop-Started {
    foreach ($process in $started) { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(3000) | Out-Null } }
    $started.Clear()
    foreach ($profile in $chromeProfiles) {
        # Only the processes of this script's own profiles, never anyone else's Chrome.
        Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    $chromeProfiles.Clear()
}

function Get-ContextSwitches([int]$id) {
    (Get-CimInstance Win32_PerfRawData_PerfProc_Thread -Filter "IDProcess = $id" | Measure-Object ContextSwitchesPersec -Sum).Sum
}

function Get-Gpu([int]$id) {
    $engines = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "pid_$($id)_*" }
    $memory = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "pid_$($id)_*" }
    [pscustomobject]@{
        Percent = [double](($engines | Measure-Object UtilizationPercentage -Sum).Sum)
        DedicatedMB = [math]::Round((($memory | Measure-Object DedicatedUsage -Sum).Sum) / 1MB, 1)
    }
}

function Measure-Process([Diagnostics.Process]$process) {
    Start-Sleep -Seconds $Settle
    $process.Refresh()
    $cpu0 = $process.TotalProcessorTime; $switches0 = Get-ContextSwitches $process.Id; $clock = [Diagnostics.Stopwatch]::StartNew()
    $memory = [Collections.Generic.List[double]]::new(); $gpu = [Collections.Generic.List[double]]::new(); $gpuMemory = 0.0; $threads = 0
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Seconds 2
        $process.Refresh()
        $memory.Add($process.PrivateMemorySize64 / 1MB)
        $threads = [math]::Max($threads, $process.Threads.Count)
        $g = Get-Gpu $process.Id
        $gpu.Add($g.Percent); $gpuMemory = [math]::Max($gpuMemory, $g.DedicatedMB)
    }
    $process.Refresh()
    $elapsed = $clock.Elapsed.TotalSeconds
    [pscustomobject]@{
        CpuPercent = [math]::Round(($process.TotalProcessorTime - $cpu0).TotalSeconds / $elapsed / [Environment]::ProcessorCount * 100, 3)
        CpuPercentOfOneCore = [math]::Round(($process.TotalProcessorTime - $cpu0).TotalSeconds / $elapsed * 100, 2)
        ContextSwitchesPerSecond = [math]::Round(((Get-ContextSwitches $process.Id) - $switches0) / $elapsed, 1)
        PrivateMB = [math]::Round(($memory | Measure-Object -Average).Average, 1)
        Threads = $threads
        GpuPercent = [math]::Round(($gpu | Measure-Object -Average).Average, 2)
        GpuDedicatedMB = $gpuMemory
    }
}

$scenarios = [ordered]@{
    tray     = @{ Text = 'Tray only'; Settings = @{}; Setup = {} }
    toolbar  = @{ Text = 'Toolbar on'; Settings = @{ ShowToolbar = $true }; Setup = {} }
    pinned   = @{ Text = 'One pinned window with its border'; Settings = @{ HighlightPinned = $true }
                  Setup = { Start-Target 'WinScreen perf target'; Send-WinScreen 'TogglePin' 'title:WinScreen perf target' } }
    view     = @{ Text = 'One Standard view of a still window'; Settings = @{ Pip = @{ Backend = 'Dwm' } }
                  Setup = { Start-Target 'WinScreen perf target'; Send-WinScreen 'OpenPip' 'title:WinScreen perf target' } }
    smooth30 = @{ Text = 'One Smooth view of a video, 30 frames'; Settings = @{ Pip = @{ Backend = 'Wgc'; FrameRate = 30 } }
                  Setup = { Start-Video 'WinScreen perf video'; Send-WinScreen 'OpenPip' 'title:WinScreen perf video' } }
    smooth60 = @{ Text = 'One Smooth view of a video, 60 frames'; Settings = @{ Pip = @{ Backend = 'Wgc'; FrameRate = 60 } }
                  Setup = { Start-Video 'WinScreen perf video'; Send-WinScreen 'OpenPip' 'title:WinScreen perf video' } }
    smooth3  = @{ Text = 'Three Smooth views of videos, 60 frames'; Settings = @{ Pip = @{ Backend = 'Wgc'; FrameRate = 60 } }
                  Setup = { foreach ($i in 1..3) { Start-Video "WinScreen perf video $i" (60 * $i) (60 * $i); Send-WinScreen 'OpenPip' "title:WinScreen perf video $i" } } }
    dark     = @{ Text = 'Dark on a scrolling page'; Settings = @{ ExperimentalFeatures = $true }
                  Setup = { Start-Video 'WinScreen perf page' -Scrolling; Send-WinScreen 'ToggleDark' 'title:WinScreen perf page' } }
    visit    = @{ Text = 'One view with the pointer moving over it'; Settings = @{ Pip = @{ Backend = 'Dwm' } }
                  Setup = { Start-Video 'WinScreen perf page' -Scrolling; Send-WinScreen 'OpenPip' 'title:WinScreen perf page' } }
}

$results = [Collections.Generic.List[object]]::new()
try {
    foreach ($name in $Scenario) {
        $definition = $scenarios[$name]
        if (-not $definition) { throw "No scenario called $name." }
        if ($name -eq 'visit' -and -not $MovePointer) { Write-Host "skipped $name (needs -MovePointer)"; continue }
        Write-Host "== ${name}: $($definition.Text)"
        Write-Settings $definition.Settings
        $app = Start-WinScreen
        try {
            & $definition.Setup
            $sweep = $null
            if ($name -eq 'visit') {
                # The view opens at the bottom right of the main display; sweep over it for the whole measurement.
                $sweep = Start-Job { param($w, $h) $end = (Get-Date).AddSeconds(200)
                    Add-Type -Namespace Perf -Name Mouse -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);'
                    $i = 0; while ((Get-Date) -lt $end) { $i++; [Perf.Mouse]::SetCursorPos($w - 300 + ($i * 7) % 250, $h - 220 + ($i * 3) % 150); Start-Sleep -Milliseconds 16 } } `
                    -ArgumentList ([Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Right), ([Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Bottom)
            }
            $measured = Measure-Process $app
            if ($sweep) { Stop-Job $sweep; Remove-Job $sweep }
            $row = [ordered]@{ Scenario = $name; Description = $definition.Text; Seconds = $Seconds }
            foreach ($property in $measured.PSObject.Properties) { $row[$property.Name] = $property.Value }
            $results.Add([pscustomobject]$row)
        }
        finally {
            if (-not $app.HasExited) { Stop-WinScreen $app }
            Stop-Started
        }
    }
}
finally {
    Stop-Started
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

$environment = [ordered]@{
    windows = [Environment]::OSVersion.Version.ToString()
    build = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild
    processors = [Environment]::ProcessorCount
    displays = @([Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.Bounds.Width)x$($_.Bounds.Height)" })
    adapters = @(Get-CimInstance Win32_VideoController | ForEach-Object Name)
    commit = (& git -C $root rev-parse HEAD).Trim()
    clean = -not (& git -C $root status --porcelain --untracked-files=no)
    seconds = $Seconds
}
[IO.File]::WriteAllText((Join-Path $out 'results.json'), ([ordered]@{ environment = $environment; results = $results } | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

"| Scenario | CPU (% of all cores) | CPU (% of one core) | Context switches/s | Private MB | Threads | GPU % | GPU MB |"
"|---|---|---|---|---|---|---|---|"
foreach ($r in $results) {
    "| $($r.Description) | $($r.CpuPercent) | $($r.CpuPercentOfOneCore) | $($r.ContextSwitchesPerSecond) | $($r.PrivateMB) | $($r.Threads) | $($r.GpuPercent) | $($r.GpuDedicatedMB) |"
}
"Results: $($out.FullName)"
