<#
.SYNOPSIS
  Builds a Windows test VM from an ISO with no clicks, and saves a signed-in "clean" checkpoint.

.DESCRIPTION
  Applies the chosen edition from the ISO straight onto a new virtual disk, adds an answer file that
  creates a local test account and signs it in automatically, copies in the .NET SDK from this PC,
  then boots the VM in Hyper-V. Once the desktop is up it turns off sleep, the lock screen, updates
  and tips, registers the task that runs tests in that desktop, and takes a standard checkpoint
  while the VM runs. Invoke-TestVmRun.ps1 goes back to that checkpoint before and after every run.

  The VM has no network adapter connected, so Windows can't update or restart in the middle of a run.

  Run it from an elevated PowerShell. It takes 15-30 minutes. Running it again with -Force
  replaces the VM, which is the way to refresh it (for example after a new .NET SDK, or before an
  evaluation edition runs out: restoring a checkpoint doesn't stop that clock).

  The sign-in is a random password saved for this Windows user only, under
  %LOCALAPPDATA%\WinScreenTestVm\<Name>, along with the virtual disk. That folder is separate from the
  installed WinScreen, so uninstalling WinScreen leaves the VM alone.

.EXAMPLE
  .\New-TestVm.ps1 -IsoPath D:\ISO\Win10_22H2_English_x64.iso

.EXAMPLE
  .\New-TestVm.ps1 -IsoPath D:\ISO\Win10_22H2_English_x64.iso -Name WinScreen-Win10-150 -ScalePercent 150

.EXAMPLE
  .\New-TestVm.ps1 -Reprepare
  Runs the desktop preparation again on the existing VM and replaces its checkpoint. Takes a few minutes.
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$IsoPath,
    [string]$Name = 'WinScreen-Win10',
    # Matched against the image names in the ISO; the list is shown when nothing matches.
    [string]$Edition = 'Windows 10 Pro',
    [string]$ProductKey,
    [string]$Locale = 'en-US',
    [string]$TimeZone = (Get-TimeZone).Id,
    [ValidateRange(2, 64)][int]$MemoryGB = 6,
    [ValidateRange(1, 64)][int]$ProcessorCount = 4,
    [ValidateRange(40, 2048)][int]$DiskGB = 64,
    [int]$Width = 1920,
    [int]$Height = 1080,
    [ValidateSet(100, 125, 150, 175, 200)][int]$ScalePercent = 100,
    [string]$CheckpointName = 'clean',
    # Folder for the virtual disk. Defaults to the VM's state folder.
    [string]$VmPath,
    [string]$DotnetRoot,
    [int]$SetupTimeoutMinutes = 45,
    [switch]$Force,
    # Prepare an existing VM again and replace its checkpoint, without reinstalling Windows.
    [switch]$Reprepare
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestVm.Common.ps1')
Assert-HyperVAccess

if (-not $DotnetRoot) { $DotnetRoot = Find-HostDotnet }
$state = Get-TestVmStateFolder $Name
if (-not $VmPath) { $VmPath = Join-Path $state 'vm' }
$vhdPath = Join-Path $VmPath "$Name.vhdx"
$userName = 'tester'

function Step([string]$text) { Write-Output "== $text" }

# Starts the VM, prepares the desktop, restarts Windows properly, checks the setup survived,
# and saves the signed-in checkpoint.
function Complete-TestVm([int]$FirstStartSeconds, [datetime]$Created, [string]$Iso) {
    Start-VM -VM $vm
    $session = Wait-TestVmSession -Name $Name -Credential $credential -TimeoutSeconds $FirstStartSeconds
    try {
        Wait-TestVmDesktop -Session $session -TimeoutSeconds 900
        Step 'Preparing the desktop for tests'
        Invoke-Command -Session $session -FilePath (Join-Path $PSScriptRoot 'guest\Prepare-Guest.ps1') -ArgumentList $userName
        $guest = Invoke-Command -Session $session -ScriptBlock {
            $os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
            [pscustomobject]@{ Product = $os.ProductName; Edition = $os.EditionID; Build = "$($os.CurrentBuildNumber).$($os.UBR)" }
        }
        Step 'Restarting Windows so every setting takes effect'
        # A Windows restart, not Restart-VM: that is a hard reset and can lose what was just saved.
        $bootBefore = Invoke-Command -Session $session -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
        try { Invoke-Command -Session $session -ScriptBlock { Restart-Computer -Force } -ErrorAction Stop } catch { }
    }
    finally { Remove-PSSession $session -ErrorAction SilentlyContinue }

    Start-Sleep -Seconds 15
    $session = Wait-Until -What 'Windows to restart' -TimeoutSeconds 900 -PollSeconds 10 -Condition {
        $candidate = New-PSSession -VMName $Name -Credential $credential -ErrorAction Stop
        $boot = Invoke-Command -Session $candidate -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime }
        if ($boot -ne $bootBefore) { $candidate } else { Remove-PSSession $candidate; $null }
    }
    try {
        Wait-TestVmDesktop -Session $session -TimeoutSeconds 600
        $missing = Invoke-Command -Session $session -ScriptBlock {
            param($task)
            $problems = @()
            if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) { $problems += "scheduled task '$task'" }
            if ((Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization' -ErrorAction SilentlyContinue).NoLockScreen -ne 1) { $problems += 'lock screen setting' }
            $problems
        } -ArgumentList $script:GuestTaskName
        if ($missing) { throw "The setup didn't survive the restart: $($missing -join ', ') missing." }
        # Let the first-sign-in work finish so the checkpoint is quiet.
        Start-Sleep -Seconds 90
        Wait-TestVmDesktop -Session $session -TimeoutSeconds 120
    }
    finally { Remove-PSSession $session -ErrorAction SilentlyContinue }

    Step "Saving checkpoint '$CheckpointName'"
    Get-VMSnapshot -VMName $Name -Name $CheckpointName -ErrorAction SilentlyContinue | Remove-VMSnapshot
    Checkpoint-VM -VM $vm -SnapshotName $CheckpointName
    Stop-VM -VM $vm -TurnOff -Force
    Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false

    [ordered]@{
        name = $Name
        created = $Created.ToString('o')
        prepared = (Get-Date).ToString('o')
        iso = $Iso
        product = $guest.Product
        edition = $guest.Edition
        build = $guest.Build
        checkpoint = $CheckpointName
        scalePercent = $ScalePercent
        resolution = "${Width}x${Height}"
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $state 'vm.json') -Encoding UTF8

    Write-Output "Done: '$Name' runs $($guest.Product) $($guest.Build). Run tests with .\Invoke-TestVmRun.ps1 -Name $Name"
}

if ($Reprepare) {
    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if (-not $vm) { throw "No VM named '$Name' to prepare again." }
    $credential = Get-TestVmCredential $Name
    $info = Join-Path $state 'vm.json'
    $previous = if (Test-Path $info) { Get-Content -Raw $info | ConvertFrom-Json } else { $null }
    Stop-VM -VM $vm -TurnOff -Force -ErrorAction SilentlyContinue
    if (Get-VMSnapshot -VMName $Name -Name $CheckpointName -ErrorAction SilentlyContinue) {
        Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false
    }
    Step "Preparing '$Name' again"
    Complete-TestVm -FirstStartSeconds 900 -Created $(if ($previous) { [datetime]$previous.created } else { Get-Date }) -Iso $(if ($previous) { $previous.iso } else { '' })
    return
}
if (-not $IsoPath) { throw 'Pass -IsoPath, or -Reprepare to fix an existing VM.' }
$IsoPath = (Resolve-Path -LiteralPath $IsoPath).Path

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    if (-not $Force) { throw "VM '$Name' already exists. Pass -Force to replace it." }
    Step "Removing the old '$Name'"
    Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
    Get-VMSnapshot -VMName $Name | Remove-VMSnapshot
    Remove-VM -Name $Name -Force
    Start-Sleep -Seconds 2
}
if (Test-Path -LiteralPath $vhdPath) {
    if (-not $Force) { throw "$vhdPath already exists. Pass -Force to replace it." }
    Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $vhdPath -Force
}
New-Item -ItemType Directory -Path $VmPath -Force | Out-Null

$password = New-TestVmPassword
$credential = [pscredential]::new($userName, (ConvertTo-SecureString $password -AsPlainText -Force))

$iso = $null
$disk = $null
try {
    Step 'Reading the ISO'
    $iso = Mount-DiskImage -ImagePath $IsoPath -PassThru
    $isoLetter = ($iso | Get-Volume).DriveLetter
    $image = @("$($isoLetter):\sources\install.wim", "$($isoLetter):\sources\install.esd") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $image) { throw 'The ISO has no sources\install.wim or install.esd.' }
    $images = @(Get-WindowsImage -ImagePath $image)
    $chosen = $images | Where-Object ImageName -eq $Edition | Select-Object -First 1
    if (-not $chosen) {
        throw "No '$Edition' in this ISO. It has: $(($images.ImageName) -join ', '). Pass one of them as -Edition."
    }
    $details = Get-WindowsImage -ImagePath $image -Index $chosen.ImageIndex
    if ($details.Architecture -ne 9) { throw "'$Edition' in this ISO isn't x64." }
    Step "Using $($chosen.ImageName), build $($details.Version)"

    Step "Creating a $DiskGB GB disk"
    New-VHD -Path $vhdPath -SizeBytes ($DiskGB * 1GB) -Dynamic | Out-Null
    $disk = Mount-VHD -Path $vhdPath -Passthru | Get-Disk
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
    # Windows creates a reserved partition by itself on some versions; start from a clean table.
    Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue | Remove-Partition -Confirm:$false
    # Letters are added only after formatting, so Explorer doesn't pop up "format this disk?" prompts.
    $system = New-Partition -DiskNumber $disk.Number -Size 260MB
    $system | Format-Volume -FileSystem FAT32 -NewFileSystemLabel System -Confirm:$false | Out-Null
    New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
    $windows = New-Partition -DiskNumber $disk.Number -UseMaximumSize
    $windows | Format-Volume -FileSystem NTFS -NewFileSystemLabel Windows -Confirm:$false | Out-Null
    foreach ($partition in $system, $windows) {
        Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AssignDriveLetter
    }
    $systemLetter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $system.PartitionNumber).DriveLetter
    $windowsLetter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $windows.PartitionNumber).DriveLetter
    if (-not $systemLetter -or -not $windowsLetter) { throw 'Windows did not give the new partitions drive letters.' }

    Step 'Applying Windows (several minutes)'
    Expand-WindowsImage -ImagePath $image -Index $chosen.ImageIndex -ApplyPath "$($windowsLetter):\" | Out-Null
    # Use the image's own bcdboot first: a newer host's copy can expect boot files an older Windows doesn't have.
    $bootTools = @("$($windowsLetter):\Windows\System32\bcdboot.exe", (Join-Path $env:SystemRoot 'System32\bcdboot.exe')) |
        Where-Object { Test-Path $_ }
    $booted = $false
    foreach ($tool in $bootTools) {
        $output = & $tool "$($windowsLetter):\Windows" /s "$($systemLetter):" /f UEFI /v 2>&1
        if ($LASTEXITCODE -eq 0) { $booted = $true; break }
        Write-Output "bcdboot ($tool) failed:"
        $output | Select-Object -Last 15 | ForEach-Object { Write-Output "  $_" }
    }
    if (-not $booted) { throw 'Could not make the new disk bootable.' }

    Step 'Adding the answer file, .NET and the test scripts'
    $panther = "$($windowsLetter):\Windows\Panther"
    New-Item -ItemType Directory -Path $panther -Force | Out-Null
    New-TestVmUnattend -UserName $userName -Password $password -Locale $Locale -TimeZone $TimeZone -ProductKey $ProductKey |
        Set-Content -LiteralPath (Join-Path $panther 'unattend.xml') -Encoding UTF8
    & robocopy.exe $DotnetRoot "$($windowsLetter):\Program Files\dotnet" /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ".NET copy failed (robocopy $LASTEXITCODE)." }
    & robocopy.exe (Join-Path $PSScriptRoot 'guest') "$($windowsLetter):\WinScreenTest\guest" /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Script copy failed (robocopy $LASTEXITCODE)." }
    if ($ScalePercent -ne 100) {
        # Set in the default profile, which the test account is created from.
        $hive = "$($windowsLetter):\Users\Default\NTUSER.DAT"
        & reg.exe load HKU\WinScreenDefault $hive | Out-Null
        try {
            & reg.exe add 'HKU\WinScreenDefault\Control Panel\Desktop' /v LogPixels /t REG_DWORD /d ([int](96 * $ScalePercent / 100)) /f | Out-Null
            & reg.exe add 'HKU\WinScreenDefault\Control Panel\Desktop' /v Win8DpiScaling /t REG_DWORD /d 1 /f | Out-Null
        }
        finally {
            [GC]::Collect(); & reg.exe unload HKU\WinScreenDefault | Out-Null
        }
    }

    # Make the first partition the EFI system partition now that bcdboot has written to it.
    Set-Partition -DiskNumber $disk.Number -PartitionNumber $system.PartitionNumber -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
}
finally {
    if ($disk) { Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue }
    if ($iso) { Dismount-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue | Out-Null }
}

Step "Creating VM '$Name'"
$vm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -VHDPath $vhdPath -Path $VmPath
Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
Set-VMProcessor -VM $vm -Count $ProcessorCount
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
Set-VM -VM $vm -AutomaticCheckpointsEnabled $false -CheckpointType Standard -AutomaticStartAction Nothing -AutomaticStopAction TurnOff
Set-VMVideo -VM $vm -HorizontalResolution $Width -VerticalResolution $Height -ResolutionType Single
Get-VMNetworkAdapter -VM $vm | Disconnect-VMNetworkAdapter
Enable-VMIntegrationService -VM $vm -Name 'Guest Service Interface'
$credential | Export-Clixml -LiteralPath (Join-Path $state 'credential.xml')

Step "First start: Windows sets itself up (up to $SetupTimeoutMinutes minutes)"
Complete-TestVm -FirstStartSeconds ($SetupTimeoutMinutes * 60) -Created (Get-Date) -Iso $IsoPath
