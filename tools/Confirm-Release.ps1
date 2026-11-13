<#
.SYNOPSIS
  Says whether a commit may be tagged as a release, from the test runs recorded for it in TestResults.

.DESCRIPTION
  Refuses (exit code 1, with every reason) unless TestResults holds, for the commit, built from it with nothing
  uncommitted:
  - a full pair run in the Windows 10 VMs (tools\testvm\Invoke-TestVmPair.ps1) with no failures;
  - a Windows 11 run of every desktop test on this PC (tools\Invoke-HostRun.ps1 -All) with no failures, or whose
    every failure passed again in a later run on this PC for the same commit (a targeted rerun of just those);
  - a logic run (tools\Invoke-LogicRun.ps1) with no failures.
  The newest run of each kind for the commit is the one that counts (for Windows 11, the newest full one). It also checks the version in
  Directory.Build.props and, unless -Commit is given, that nothing is uncommitted now and the tag is new.

  When everything is there it prints lines to end the tag message with (the release workflow checks the first one
  for a stable release and leaves them all out of the release notes), then what did not run: skipped tests and
  tests that returned early. For a stable version it also lists what stable means, for the owner to confirm.

  The order for a release: set the version in Directory.Build.props, commit, run the three, run this, tag.

.EXAMPLE
  .\tools\Confirm-Release.ps1 -Version 0.11.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    # The commit to check. Default: HEAD, and then the working tree and tags are checked too.
    [string]$Commit,
    [string]$ResultsRoot,
    [string]$PropsFile
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'TestRuns.ps1')
if (-not $ResultsRoot) { $ResultsRoot = Join-Path $repo 'TestResults' }
if (-not $PropsFile) { $PropsFile = Join-Path $repo 'Directory.Build.props' }

$problems = New-Object System.Collections.Generic.List[string]
$version = $Version.TrimStart('v')

# The same rule as the release workflow's tag check.
if ($version -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(alpha|beta|rc)(\.[1-9][0-9]*)?)?\z') {
    $problems.Add("$version isn't a release version: it looks like 1.2.3, or 1.2.3-beta for a prerelease.")
}
$stable = -not $version.Contains('-')
$props = if (Test-Path -LiteralPath $PropsFile) { Get-Content -Raw -LiteralPath $PropsFile } else { '' }
if ($props -match '<Version>([^<]+)</Version>') {
    if ($Matches[1].Trim() -ne $version) {
        $problems.Add("Directory.Build.props says $($Matches[1].Trim()). Set it to $version and commit first, then make the runs.")
    }
}
else { $problems.Add("No <Version> in $PropsFile.") }

if (-not $Commit) {
    $state = Get-SourceState $repo
    if (-not $state.commit) {
        Write-Output "Not ready to tag v${version}: $($state.note)"
        exit 1
    }
    $Commit = $state.commit
    if (-not $state.clean) {
        $problems.Add("There are uncommitted changes. Commit them; the runs must test what is tagged. $($state.note)".Trim())
    }
    $tags = @(& git -C $repo tag --list "v$version")
    if ($LASTEXITCODE -ne 0) { $problems.Add('Git could not list the tags.') }
    elseif ($tags.Count -gt 0) { $problems.Add("The tag v$version exists already.") }
}
$short = if ($Commit.Length -ge 7) { $Commit.Substring(0, 7) } else { $Commit }

function Read-Record([string]$Folder) {
    $file = Join-Path $Folder 'source.json'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { return Get-Content -Raw -LiteralPath $file | ConvertFrom-Json } catch { return $null }
}

# The newest results folder recorded for the commit (folders are named by the time they started), of those $Wanted
# accepts when it is given.
function Find-Run([string]$Folder, [string]$Pattern, [scriptblock]$Wanted) {
    foreach ($candidate in @(Get-ChildItem -LiteralPath $Folder -Directory -Filter $Pattern -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
        $record = Read-Record $candidate.FullName
        if ($record -and $record.commit -eq $Commit -and (-not $Wanted -or (& $Wanted $record))) { return $candidate }
    }
    return $null
}

function Get-ExitCode([string]$Folder) {
    $exit = Join-Path $Folder 'exitcode.txt'
    if (-not (Test-Path -LiteralPath $exit)) { return $null }
    return "$(Get-Content -Raw -LiteralPath $exit)".Trim()
}

# Checks one run's folder; returns its outcomes. $Suites are the .trx files it must have. $Forgiven are failures
# ("suite|test") that passed again in a later run (see the Windows 11 part).
function Test-Run([string]$Label, [string]$Folder, [string[]]$Suites, [string[]]$Forgiven = @()) {
    $record = Read-Record $Folder
    if (-not $record -or $record.commit -ne $Commit) { $problems.Add("${Label}: no source.json for $short in $Folder."); return @() }
    if (-not $record.clean) { $problems.Add("${Label}: built or run with uncommitted changes ($Folder).") }
    $outcomes = @(Get-RunOutcomes $Folder)
    foreach ($suite in $Suites) {
        if (-not ($outcomes | Where-Object Suite -eq $suite)) { $problems.Add("${Label}: no $suite.trx in $Folder.") }
    }
    $left = 0
    foreach ($outcome in $outcomes) {
        $failed = @($outcome.FailedTests | Where-Object { $Forgiven -notcontains "$($outcome.Suite)|$_" })
        $left += $failed.Count
        if ($failed.Count -gt 0) { $problems.Add("${Label}: $($failed.Count) $($outcome.Suite) tests failed ($Folder).") }
        if ($outcome.Passed -eq 0) { $problems.Add("${Label}: no $($outcome.Suite) test passed ($Folder).") }
    }
    $exit = Get-ExitCode $Folder
    # A run whose every failure passed again ended with the failures' exit code, and that is all it says.
    if ($null -eq $exit) { $problems.Add("${Label}: the run didn't finish (no exitcode.txt in $Folder).") }
    elseif ($exit -ne '0' -and -not ($left -eq 0 -and $Forgiven.Count -gt 0)) { $problems.Add("${Label}: exit code $exit ($Folder).") }
    return $outcomes
}

# Failures of a full Windows 11 run that passed again later on this desktop, as the owner asked (13 Nov 2026): in a
# later run for the same commit, built clean, on Windows 11, not as administrator, that finished with no failures
# of its own; the same test (theory rows by their arguments) in the same suite, really run (not returned early).
function Get-PassedAgain([string]$HostFolder, $Full) {
    $failed = @(Get-RunOutcomes $Full.FullName | ForEach-Object { $suite = $_.Suite; $_.FailedTests | ForEach-Object { "$suite|$_" } })
    $passed = @{}
    if ($failed.Count -eq 0) { return $passed }
    foreach ($later in @(Get-ChildItem -LiteralPath $HostFolder -Directory | Where-Object { $_.Name -gt $Full.Name } | Sort-Object Name)) {
        $record = Read-Record $later.FullName
        if (-not $record -or $record.commit -ne $Commit -or -not $record.clean -or $record.windows -ne '11' -or $record.administrator) { continue }
        if ((Get-ExitCode $later.FullName) -ne '0') { continue }
        $outcomes = @(Get-RunOutcomes $later.FullName)
        if (@($outcomes | Where-Object { $_.Failed -gt 0 }).Count -gt 0) { continue }
        foreach ($outcome in $outcomes) {
            foreach ($test in $outcome.PassedTests) {
                if ($outcome.EarlyTests -contains ($test -split '\(', 2)[0]) { continue }
                $key = "$($outcome.Suite)|$test"
                if ($failed -contains $key -and -not $passed.ContainsKey($key)) { $passed[$key] = $later.Name }
            }
        }
    }
    return $passed
}

function Sum($Outcomes) {
    $parts = foreach ($group in $Outcomes | Group-Object Suite | Sort-Object Name) {
        $passed = ($group.Group | Measure-Object Passed -Sum).Sum
        "$($group.Name) $passed passed"
    }
    $parts -join ', '
}

$gate = New-Object System.Collections.Generic.List[string]
$notRun = New-Object System.Collections.Generic.List[string]
function Add-NotRun([string]$Label, $Outcomes) {
    foreach ($line in @(Get-NotRunLines $Outcomes)) { $notRun.Add("${Label} $line") }
}

# Windows 10: both halves of a full pair run.
$pair = Find-Run (Join-Path $ResultsRoot 'vm') 'pair-*'
if (-not $pair) { $problems.Add("No pair run for $short. Run tools\testvm\Invoke-TestVmPair.ps1 (about 25 minutes).") }
else {
    $record = Read-Record $pair.FullName
    if (-not $record.clean) { $problems.Add("Windows 10: built with uncommitted changes ($($pair.FullName)).") }
    $all = @()
    $halves = @(Get-ChildItem -LiteralPath $pair.FullName -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'source.json') })
    if ($halves.Count -ne 2) { $problems.Add("Windows 10: $($halves.Count) VM results in $($pair.FullName), not 2.") }
    foreach ($half in $halves) {
        $run = Read-Record $half.FullName
        if ($run -and $run.filter) { $problems.Add("Windows 10: $($half.Name) ran only $($run.filter).") }
        $all += @(Test-Run "Windows 10 ($($half.Name))" $half.FullName @('packaged'))
        Add-NotRun "Windows 10 ($($half.Name))" @(Get-RunOutcomes $half.FullName)
    }
    if (-not ($all | Where-Object Suite -eq 'desktop')) { $problems.Add("Windows 10: neither VM ran the desktop suite ($($pair.FullName)).") }
    $gate.Add("Release-Gate-Windows-10: $($pair.Name) ($(Sum $all))")
}

# Windows 11: every desktop test on this PC. The newest full run counts; a later targeted run can only pass again
# what it failed.
$hostFolder = Join-Path $ResultsRoot 'host'
$hostRun = Find-Run $hostFolder '*' { param($record) $record.all }
if (-not $hostRun) { $problems.Add("No full Windows 11 run for $short. Run tools\Invoke-HostRun.ps1 -All -Suite Both, once the PC is free.") }
else {
    $record = Read-Record $hostRun.FullName
    if ($record.windows -ne '11') { $problems.Add("Windows 11: $($hostRun.Name) ran on Windows $($record.windows).") }
    if ($record.administrator) { $problems.Add("Windows 11: $($hostRun.Name) ran as administrator.") }
    $again = Get-PassedAgain $hostFolder $hostRun
    $outcomes = @(Test-Run 'Windows 11' $hostRun.FullName @('desktop') @($again.Keys))
    Add-NotRun 'Windows 11' $outcomes
    $passedAgain = if ($again.Count -gt 0) { "; $($again.Count) failed, passed again in $(@($again.Values | Sort-Object -Unique) -join ', ')" } else { '' }
    $gate.Add("Release-Gate-Windows-11: $($hostRun.Name) ($(Sum $outcomes)$passedAgain)")
}

# Logic.
$logic = Find-Run (Join-Path $ResultsRoot 'logic') '*'
if (-not $logic) { $problems.Add("No logic run for $short. Run tools\Invoke-LogicRun.ps1.") }
else {
    $outcomes = @(Test-Run 'Logic' $logic.FullName @('logic'))
    Add-NotRun 'Logic' $outcomes
    $gate.Add("Release-Gate-Logic: $($logic.Name) ($(Sum $outcomes))")
}

if ($problems.Count -gt 0) {
    Write-Output "Not ready to tag v$version at $short."
    $problems | ForEach-Object { Write-Output "- $_" }
    exit 1
}

Write-Output "Ready to tag v$version at $short."
Write-Output ''
Write-Output 'End the tag message with these lines, after a blank line. The release notes leave them out.'
Write-Output ''
Write-Output "Release-Gate: $Commit"
$gate | ForEach-Object { Write-Output $_ }
Write-Output ''
if ($notRun.Count -gt 0) {
    Write-Output 'Did not run (check none of these is a reason not to release):'
    $notRun | ForEach-Object { Write-Output "- $_" }
}
else { Write-Output 'Every test ran.' }
if ($stable) {
    Write-Output ''
    Write-Output 'A stable release also needs, from the owner:'
    Write-Output '- no open bug that leaves a window hidden, unclickable or off screen;'
    Write-Output '- an update from the previous stable release checked in a VM (none before the first);'
    Write-Output '- Interactive PiP graduated for Chromium browsers at least, or clearly labelled;'
    Write-Output "- the README's status line changed."
}
exit 0
