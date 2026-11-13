<#
.SYNOPSIS
  Runs desktop tests on this PC's own desktop (Windows 11), the one machine that can't be reset.

.DESCRIPTION
  Everything that can run in the Windows 10 test VMs runs there. This is for Windows 11 behaviour, the real
  graphics card and installed apps. The tests open their own windows and drive the mouse and keyboard, so
  nobody may use the PC while they run.

  1. Checks first, and stops with the reason (exit code 3) if this PC isn't ready: the shell runs as
     administrator; another WinScreen, or a test window from an earlier run, is open (it is named, never
     closed by this script); an app fills the screen in front; the hover helper is missing from the build.
  2. Builds (unless -SkipBuild), then says what it is about to run. Without -GoAhead it stops there (exit
     code 2): the person whose PC this is says yes first, then the same command runs again with -GoAhead.
  3. With -GoAhead: checks again, records the conditions in environment.json (Windows build, displays and
     scale, graphics, input languages, administrator or not, commit), runs, and lists what did not run.

  Results go to TestResults\host\<time>: a .trx per suite, runner.log, environment.json, not-run.txt.
  Exit code 0 when every test passed.

.EXAMPLE
  .\tools\Invoke-HostRun.ps1 -Filter "FullyQualifiedName~DarkModeTests"

.EXAMPLE
  .\tools\Invoke-HostRun.ps1 -Filter "FullyQualifiedName~DarkModeTests" -SkipBuild -GoAhead

.EXAMPLE
  .\tools\Invoke-HostRun.ps1 -Suite Packaged -Filter "FullyQualifiedName~InstalledAppTests" -InstalledApps
#>
[CmdletBinding()]
param(
    # The tests to run, for example "FullyQualifiedName~DarkModeTests". Required unless -All.
    [string]$Filter,
    # Every desktop test. A full run takes about half an hour; agree it first.
    [switch]$All,
    # Desktop: everything but Category=Interactive. Packaged: Category=Interactive against the published app.
    [ValidateSet('Desktop', 'Packaged', 'Both')][string]$Suite = 'Desktop',
    # Also run the tests against apps installed on this PC (WINSCREEN_TEST_INSTALLED_APPS=1).
    [switch]$InstalledApps,
    # The person whose PC this is said yes, and is keeping off the mouse and keyboard.
    [switch]$GoAhead,
    [switch]$SkipBuild,
    [string]$ResultsDirectory,
    [int]$HangMinutes = 3
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $Filter -and -not $All) { throw 'Name the tests with -Filter (targeted runs by default), or pass -All.' }
if ($Filter -and $All) { throw 'Pass -Filter or -All, not both.' }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class HostRunDesktop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT clip, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    private static readonly IntPtr PerMonitorAware = new IntPtr(-4);

    private static MONITORINFOEX Info(IntPtr monitor)
    {
        var info = new MONITORINFOEX();
        info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
        GetMonitorInfo(monitor, ref info);
        return info;
    }

    /// <summary>Each display: name, size and place in physical pixels, scale, and whether it is the main one.</summary>
    public static string[] Displays()
    {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorAware);
        try
        {
            var displays = new List<string>();
            MonitorEnumProc each = delegate (IntPtr monitor, IntPtr hdc, ref RECT clip, IntPtr data)
            {
                MONITORINFOEX info = Info(monitor);
                uint x, y;
                string scale = GetDpiForMonitor(monitor, 0, out x, out y) == 0
                    ? Math.Round(x * 100.0 / 96) + "%" : "unknown scale";
                RECT r = info.rcMonitor;
                displays.Add(string.Format("{0} {1}x{2} at ({3},{4}) {5}{6}", info.szDevice, r.Right - r.Left,
                    r.Bottom - r.Top, r.Left, r.Top, scale, (info.dwFlags & 1) != 0 ? " main" : ""));
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
            GC.KeepAlive(each);
            return displays.ToArray();
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }

    /// <summary>The window in front when it covers its whole display (a game, a video, a presentation); 0 if none.</summary>
    public static uint FullScreenProcess(out string windowClass)
    {
        windowClass = "";
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorAware);
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            windowClass = name.ToString();
            // The desktop and the taskbar cover a display without being an app in front.
            if (windowClass == "Progman" || windowClass == "WorkerW" || windowClass == "Shell_TrayWnd" ||
                windowClass == "Shell_SecondaryTrayWnd") return 0;
            RECT shown;
            if (!GetWindowRect(window, out shown)) return 0;
            RECT screen = Info(MonitorFromWindow(window, 2)).rcMonitor;
            if (shown.Left > screen.Left || shown.Top > screen.Top || shown.Right < screen.Right || shown.Bottom < screen.Bottom)
                return 0;
            uint process;
            GetWindowThreadProcessId(window, out process);
            return process;
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }
}
'@

function Say([string]$text) { Write-Output "== $text" }

# Each returns the reasons this PC isn't ready; none means ready.
function Get-Problems {
    $problems = @()
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $problems += 'This shell runs as administrator. Desktop tests check what WinScreen does without it; start a normal shell.'
    }
    foreach ($process in @(Get-Process -Name 'WinScreen', 'WinScreen.TestTarget' -ErrorAction SilentlyContinue)) {
        $where = if ($process.Path) { $process.Path } else { 'its path is hidden, so it probably runs as administrator' }
        if ($process.ProcessName -eq 'WinScreen') {
            $problems += "WinScreen is running (process $($process.Id), $where). Close it from its tray icon first; this script never closes it."
        }
        else {
            $problems += "A test window from an earlier run is still open (process $($process.Id), $where). Close it first."
        }
    }
    $class = ''
    $full = [HostRunDesktop]::FullScreenProcess([ref]$class)
    if ($full -ne 0) {
        $name = (Get-Process -Id $full -ErrorAction SilentlyContinue).ProcessName
        $problems += "An app fills the screen in front ($name, window class $class). Leave full screen or minimize it first."
    }
    return $problems
}

function Stop-IfNotReady {
    $problems = @(Get-Problems)
    if ($problems.Count -eq 0) { return }
    Say 'Not starting'
    $problems | ForEach-Object { Write-Output "- $_" }
    exit 3
}

Stop-IfNotReady

$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# The hover helper needs a C compiler; without one the build only warns and every hover test fails.
if (-not $env:WINSCREEN_ZIG) { $env:WINSCREEN_ZIG = Join-Path $repo '.local\hover-tools\zig-windows-x86_64-0.13.0\zig.exe' }
if (-not (Test-Path $env:WINSCREEN_ZIG)) { Say 'Not starting'; Write-Output "- WINSCREEN_ZIG points at $env:WINSCREEN_ZIG, which doesn't exist."; exit 3 }
$common = @('-c', 'Release', '-p:WinScreenArchitecture=x64', '-p:UpdateRepository=DarkLian/WinScreen')
$program = Join-Path $repo 'TestResults\host\_program'
$packaged = $Suite -in 'Packaged', 'Both'

if (-not $SkipBuild) {
    Say 'Building'
    & $dotnet build (Join-Path $repo 'WinScreen.slnx') @common -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    if ($packaged) {
        Say 'Publishing the packaged program'
        if (Test-Path $program) { Remove-Item $program -Recurse -Force }
        & $dotnet publish (Join-Path $repo 'src\WinScreen\WinScreen.csproj') -o $program --self-contained true `
            -p:DebugType=embedded @common -v q -nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    }
}

$tests = Join-Path $repo 'tests\WinScreen.Tests\bin\Release\net10.0-windows10.0.26100.0\win-x64'
$missing = @()
if (-not (Test-Path (Join-Path $tests 'WinScreen.Tests.dll'))) { $missing += 'No built tests. Run without -SkipBuild.' }
elseif (-not (Test-Path (Join-Path $tests 'WinScreen.Hover.dll'))) { $missing += 'The test build has no WinScreen.Hover.dll. Build with WINSCREEN_ZIG set, without -SkipBuild.' }
if ($packaged -and -not (Test-Path (Join-Path $program 'WinScreen.exe'))) { $missing += 'No published program. Run without -SkipBuild.' }
elseif ($packaged -and -not (Test-Path (Join-Path $program 'WinScreen.Hover.dll'))) { $missing += 'The published program has no WinScreen.Hover.dll.' }
if ($missing.Count -gt 0) { Say 'Not starting'; $missing | ForEach-Object { Write-Output "- $_" }; exit 3 }

$steps = @()
$selected = if ($All) { $null } else { "($Filter)" }
if ($Suite -in 'Desktop', 'Both') {
    $steps += @{ name = 'desktop'; filter = (@('Category!=Interactive', $selected) | Where-Object { $_ }) -join '&' }
}
if ($packaged) {
    $steps += @{ name = 'packaged'; filter = (@('Category=Interactive', $selected) | Where-Object { $_ }) -join '&' }
}

$commit = (git -C $repo rev-parse --short HEAD).Trim()
if (git -C $repo status --porcelain --untracked-files=no) { $commit += ' with uncommitted changes' }

Say 'About to run on this desktop'
foreach ($step in $steps) { Write-Output "- $($step.name): $($step.filter)" }
if ($InstalledApps) { Write-Output '- Apps installed on this PC take part (WINSCREEN_TEST_INSTALLED_APPS=1).' }
Write-Output "- Code: $commit. Windows build $([Environment]::OSVersion.Version.Build)."
Write-Output '- The tests open their own windows and move the mouse and type. Nobody may use the PC until they finish.'

if (-not $GoAhead) {
    Write-Output ''
    Write-Output 'Waiting for a go-ahead: when the PC is free, run the same command with -SkipBuild -GoAhead.'
    exit 2
}

# The PC may have changed while the go-ahead was asked for.
Stop-IfNotReady

$lock = [Threading.Mutex]::new($false, 'Global\WinScreen.HostRun')
$owned = $false
try { $owned = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) { Say 'Not starting'; Write-Output '- Another host run is going.'; exit 3 }

if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repo ('TestResults\host\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$ResultsDirectory = (Resolve-Path $ResultsDirectory).Path

$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
[ordered]@{
    machine = 'host'
    # ProductName says "Windows 10" on Windows 11 too; the build number tells them apart.
    windows = $(if ([int]$os.CurrentBuildNumber -ge 22000) { '11' } else { '10' })
    displayVersion = $os.DisplayVersion
    build = "$($os.CurrentBuildNumber).$($os.UBR)"
    architecture = $env:PROCESSOR_ARCHITECTURE
    displays = @([HostRunDesktop]::Displays())
    gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" })
    # foreach, not the pipeline: the list comes through a pipeline as one object.
    inputLanguages = @(foreach ($language in (Get-WinUserLanguageList)) { "$($language.LanguageTag): $($language.InputMethodTips -join ', ')" })
    administrator = $false
    commit = $commit
    steps = $steps
    installedApps = [bool]$InstalledApps
    started = (Get-Date).ToString('o')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'environment.json') -Encoding UTF8

$code = 0
$savedExe = $env:WINSCREEN_EXE
$savedApps = $env:WINSCREEN_TEST_INSTALLED_APPS
Start-Transcript -Path (Join-Path $ResultsDirectory 'runner.log') -Force | Out-Null
try {
    if ($InstalledApps) { $env:WINSCREEN_TEST_INSTALLED_APPS = '1' }
    foreach ($step in $steps) {
        $env:WINSCREEN_EXE = if ($step.name -eq 'packaged') { Join-Path $program 'WinScreen.exe' } else { $savedExe }
        Say "$($step.name): $($step.filter)"
        # Windows PowerShell turns a native program's error output into errors, so don't stop on them here.
        $ErrorActionPreference = 'Continue'
        & $dotnet test (Join-Path $tests 'WinScreen.Tests.dll') --filter $step.filter `
            --logger "trx;LogFileName=$($step.name).trx" --logger 'console;verbosity=normal' --results-directory $ResultsDirectory `
            --blame-hang --blame-hang-timeout "$($HangMinutes)m" --blame-hang-dump-type none 2>&1 | ForEach-Object { "$_" }
        $stepCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($stepCode -ne 0 -and $code -eq 0) { $code = $stepCode }
    }
}
catch {
    Write-Output "Run failed: $_"
    if ($code -eq 0) { $code = 1 }
}
finally {
    $env:WINSCREEN_EXE = $savedExe
    $env:WINSCREEN_TEST_INSTALLED_APPS = $savedApps
    Stop-Transcript | Out-Null
    $lock.ReleaseMutex()
    $lock.Dispose()
}

# What did not run: skipped tests, with their reasons. A pass of a test that didn't run is not a pass.
$notRun = @()
foreach ($trx in Get-ChildItem $ResultsDirectory -Filter *.trx -ErrorAction SilentlyContinue) {
    [xml]$results = Get-Content $trx.FullName -Raw
    # Counted from each result: the summary counters leave skipped tests out of "not executed".
    # Not $all: PowerShell names ignore case, and that is the -All parameter.
    $outcomes = @($results.TestRun.Results.UnitTestResult)
    Write-Output ("{0}: {1} passed, {2} failed, {3} not run" -f $trx.BaseName, @($outcomes | Where-Object outcome -eq 'Passed').Count,
        @($outcomes | Where-Object outcome -eq 'Failed').Count, @($outcomes | Where-Object outcome -eq 'NotExecuted').Count)
    foreach ($result in @($outcomes | Where-Object { $_.outcome -eq 'NotExecuted' })) {
        $reason = $result.Output.ErrorInfo.Message
        if (-not $reason) { $reason = $result.Output.StdOut }
        $notRun += "$($trx.BaseName): $($result.testName)$(if ($reason) { " - $($reason.Trim())" })"
    }
}
# Always written, so a missing file means the run didn't get this far.
$listed = if ($notRun.Count -gt 0) { $notRun } else { @('Every selected test ran.') }
Set-Content -LiteralPath (Join-Path $ResultsDirectory 'not-run.txt') -Value $listed -Encoding UTF8
if ($notRun.Count -gt 0) { Say 'Did not run'; $notRun | ForEach-Object { Write-Output "- $_" } }
Write-Output 'Tests that return early when an app is missing are counted as passed; check the console log for them.'
Write-Output "Results: $ResultsDirectory (exit code $code)"
exit $code
