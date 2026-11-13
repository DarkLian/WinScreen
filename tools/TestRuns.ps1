# Dot-sourced by the test run scripts and Confirm-Release.ps1. Two things every run records:
# - source.json: the commit its build was made from, and whether anything was uncommitted. A build is stamped
#   (built-from.json in its output folder) with the state it started from; a run trusts the stamp only when nothing
#   in the build changed after it and HEAD is still that commit.
# - what did not run: skipped tests from each .trx, and tests that returned early, which the tests write through
#   Skipped.Because to <suite>.returned-early.txt (WINSCREEN_SKIPPED_FILE) and the .trx counts as passed.

function Get-SourceState([string]$Repo) {
    $commit = "$(& git -C $Repo rev-parse HEAD)".Trim()
    $ok = $LASTEXITCODE -eq 0
    # Untracked files count: a new .cs file is built in.
    $changes = @(& git -C $Repo status --porcelain)
    $ok = $ok -and $LASTEXITCODE -eq 0
    # So do source files Git ignores, such as local probe tests kept out by .git\info\exclude: they are built in too.
    $ignored = @(& git -C $Repo ls-files --others --ignored --exclude-standard -- src tests |
        Where-Object { $_ -match '\.(cs|csproj|props|targets|json|resx)$' -and $_ -notmatch '(^|/)(bin|obj)/' })
    $ok = $ok -and $LASTEXITCODE -eq 0
    if (-not $ok -or $commit -notmatch '\A[0-9a-f]{40}\z') {
        # Never "clean" because git said nothing.
        return [ordered]@{ commit = $null; clean = $false; changedFiles = 0; ignoredSources = @(); note = 'Git could not say which commit this is.' }
    }
    [ordered]@{ commit = $commit; clean = ($changes.Count + $ignored.Count -eq 0); changedFiles = $changes.Count
        ignoredSources = $ignored; note = $(if ($ignored.Count -gt 0) { "Built in, but ignored by Git: $($ignored -join ', ')." } else { '' }) }
}

# After a build: what it was made from, taken before it started.
function Save-BuildStamp([string]$Folder, $State) {
    $stamp = [ordered]@{ commit = $State.commit; clean = $State.clean; changedFiles = $State.changedFiles
        ignoredSources = $State.ignoredSources; stamped = (Get-Date).ToString('o') }
    $stamp | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Folder 'built-from.json') -Encoding UTF8
}

# The commit a run tests, or none with the reason. $Outputs are the built files the run uses (the test assembly,
# the published WinScreen.exe); each must sit beside a stamp no older than itself, and every stamp must agree.
function Get-RunSource([string]$Repo, [string[]]$Outputs) {
    $record = [ordered]@{ commit = $null; clean = $false; note = '' }
    $now = Get-SourceState $Repo
    if (-not $now.commit) { $record.note = $now.note; return $record }
    $clean = [bool]$now.clean
    foreach ($output in $Outputs) {
        $name = Split-Path $output -Leaf
        if (-not (Test-Path -LiteralPath $output)) { $record.note = "$name doesn't exist."; return $record }
        $stamp = Join-Path (Split-Path $output -Parent) 'built-from.json'
        if (-not (Test-Path -LiteralPath $stamp)) {
            $record.note = "$name has no built-from.json beside it, so a run script didn't build it."
            return $record
        }
        if ((Get-Item -LiteralPath $output).LastWriteTimeUtc -gt (Get-Item -LiteralPath $stamp).LastWriteTimeUtc) {
            $record.note = "$name was built again after its stamp, by something else."
            return $record
        }
        $built = Get-Content -Raw -LiteralPath $stamp | ConvertFrom-Json
        if (-not $built.commit -or $built.commit -ne $now.commit) {
            $record.note = "$name was built from $($built.commit), but HEAD is $($now.commit) now."
            return $record
        }
        $clean = $clean -and [bool]$built.clean
    }
    $record.commit = $now.commit
    $record.clean = $clean
    if (-not $clean) { $record.note = "There were uncommitted changes when it was built or run. $($now.note)".Trim() }
    return $record
}

function Save-RunRecord([string]$Folder, $Source, [hashtable]$What) {
    $record = [ordered]@{}
    foreach ($key in $Source.Keys) { $record[$key] = $Source[$key] }
    foreach ($key in $What.Keys) { $record[$key] = $What[$key] }
    $record.recorded = (Get-Date).ToString('o')
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Folder 'source.json') -Encoding UTF8
}

# Tests that returned early in one suite, one line each with how many times (a theory returns once per row).
function Get-ReturnedEarly([string]$File) {
    if (-not (Test-Path -LiteralPath $File)) { return @() }
    $lines = @(Get-Content -LiteralPath $File -Encoding UTF8 | Where-Object { $_ })
    foreach ($group in $lines | Group-Object) {
        $parts = $group.Name -split "`t", 2
        $times = if ($group.Count -gt 1) { ", $($group.Count) times" } else { '' }
        "$($parts[0]) - $($parts[1]) (returned early$times)"
    }
}

# Each suite in a results folder: passed, failed, skipped (with reasons) and returned early.
function Get-RunOutcomes([string]$Folder) {
    foreach ($trx in Get-ChildItem -LiteralPath $Folder -Filter *.trx -File -ErrorAction SilentlyContinue | Sort-Object Name) {
        [xml]$results = Get-Content -Raw -LiteralPath $trx.FullName
        # Counted from each result: the summary counters leave skipped tests out of "not executed".
        $outcomes = @($results.TestRun.Results.UnitTestResult)
        $skipped = @(foreach ($result in @($outcomes | Where-Object { $_.outcome -eq 'NotExecuted' })) {
                $reason = $result.Output.ErrorInfo.Message
                if (-not $reason) { $reason = $result.Output.StdOut }
                "$($result.testName)$(if ($reason) { " - $("$reason".Trim())" })"
            })
        [pscustomobject]@{
            Suite = $trx.BaseName
            Passed = @($outcomes | Where-Object { $_.outcome -eq 'Passed' }).Count
            # Anything else that ran counts as failed: Failed, Timeout, Aborted, Error.
            Failed = @($outcomes | Where-Object { $_.outcome -ne 'Passed' -and $_.outcome -ne 'NotExecuted' }).Count
            Skipped = $skipped
            ReturnedEarly = @(Get-ReturnedEarly (Join-Path $Folder "$($trx.BaseName).returned-early.txt"))
        }
    }
}

function Format-Outcome($Outcome) {
    '{0}: {1} passed, {2} failed, {3} not run, {4} returned early' -f $Outcome.Suite, $Outcome.Passed, $Outcome.Failed,
        $Outcome.Skipped.Count, $Outcome.ReturnedEarly.Count
}

# What did not run, one line each, named by suite.
function Get-NotRunLines($Outcomes) {
    foreach ($outcome in $Outcomes) {
        foreach ($line in @($outcome.Skipped) + @($outcome.ReturnedEarly)) { "$($outcome.Suite): $line" }
    }
}

# not-run.txt: always written, so a missing file means the run didn't get that far.
function Save-NotRun([string]$Folder, $Outcomes) {
    $lines = @(Get-NotRunLines $Outcomes)
    $listed = if ($lines.Count -gt 0) { $lines } else { @('Every selected test ran.') }
    Set-Content -LiteralPath (Join-Path $Folder 'not-run.txt') -Value $listed -Encoding UTF8
    return $lines
}
