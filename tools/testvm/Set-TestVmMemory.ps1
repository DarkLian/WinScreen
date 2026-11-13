<#
.SYNOPSIS
  Gives a test VM a different amount of memory and saves its checkpoint again with it.

.DESCRIPTION
  A checkpoint of a running VM keeps the memory it was taken with, so the memory can only change while the VM is
  off. This restores the checkpoint, shuts Windows down cleanly from inside, sets the memory, starts the VM again
  (it signs in by itself), waits for the desktop to settle, and saves the checkpoint under the same name.
  The old checkpoint is kept as <checkpoint>-<old size>gb, so nothing is lost; delete it when the new one has
  passed a test run.

.EXAMPLE
  .\Set-TestVmMemory.ps1 -Name WinScreen-Win10 -MemoryGB 4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][ValidateRange(2, 64)][int]$MemoryGB,
    [string]$CheckpointName = 'clean'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestVm.Common.ps1')
Assert-HyperVAccess
$credential = Get-TestVmCredential $Name
function Step([string]$text) { Write-Output "== $text" }

$lock = [Threading.Mutex]::new($false, "Global\WinScreen.TestVm.$Name")
$owned = $false
try { $owned = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) { throw "Another run is using '$Name'." }
$backup = $null
try {
    $old = Get-VMSnapshot -VMName $Name -Name $CheckpointName
    $oldGB = [int]($(if ($old.MemoryStartup) { $old.MemoryStartup } else { (Get-VM -Name $Name).MemoryStartup }) / 1GB)
    if ($oldGB -eq $MemoryGB) { Write-Output "'$Name' already has $MemoryGB GB in '$CheckpointName'."; return }
    $backup = "$CheckpointName-$($oldGB)gb"
    if (Get-VMSnapshot -VMName $Name -Name $backup -ErrorAction SilentlyContinue) { throw "A checkpoint named '$backup' already exists." }

    Step "Restoring '$CheckpointName' and shutting Windows down"
    Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
    Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false
    Rename-VMSnapshot -VMName $Name -Name $CheckpointName -NewName $backup
    Start-VM -Name $Name
    $session = Wait-TestVmSession -Name $Name -Credential $credential -TimeoutSeconds 300
    try { Invoke-Command -Session $session -ScriptBlock { Stop-Computer -Force } -ErrorAction Stop } catch { }
    Remove-PSSession $session -ErrorAction SilentlyContinue
    Wait-Until -What 'the VM to turn off' -TimeoutSeconds 300 -PollSeconds 5 -Condition { (Get-VM -Name $Name).State -eq 'Off' } | Out-Null

    Step "Setting $MemoryGB GB (was $oldGB GB) and starting it"
    Set-VMMemory -VMName $Name -StartupBytes ([int64]$MemoryGB * 1GB)
    Start-VM -Name $Name
    $session = Wait-TestVmSession -Name $Name -Credential $credential -TimeoutSeconds 600
    try {
        Wait-TestVmDesktop -Session $session -TimeoutSeconds 600
        $missing = Invoke-Command -Session $session -ScriptBlock {
            param($task)
            if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) { "scheduled task '$task'" }
        } -ArgumentList $script:GuestTaskName
        if ($missing) { throw "Missing after the restart: $missing." }
        # Let the sign-in work finish so the checkpoint is quiet, as New-TestVm.ps1 does.
        Start-Sleep -Seconds 90
        Wait-TestVmDesktop -Session $session -TimeoutSeconds 120
    }
    finally { Remove-PSSession $session -ErrorAction SilentlyContinue }

    Step "Saving checkpoint '$CheckpointName' (the old one is kept as '$backup')"
    Checkpoint-VM -Name $Name -SnapshotName $CheckpointName
    Stop-VM -Name $Name -TurnOff -Force
    Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false
    Write-Output "Done: '$Name' starts from '$CheckpointName' with $MemoryGB GB."
}
catch {
    # Leave the VM as it was: the old checkpoint under its own name again.
    if ($backup -and -not (Get-VMSnapshot -VMName $Name -Name $CheckpointName -ErrorAction SilentlyContinue) -and
        (Get-VMSnapshot -VMName $Name -Name $backup -ErrorAction SilentlyContinue)) {
        Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
        Rename-VMSnapshot -VMName $Name -Name $backup -NewName $CheckpointName
        Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false
        Write-Output "Failed; '$CheckpointName' is back as it was."
    }
    throw
}
finally {
    $lock.ReleaseMutex()
    $lock.Dispose()
}
