# Runs once inside a new test VM, over PowerShell Direct, as the test account (elevated).
# Keeps the console desktop awake and unlocked, and registers the task that runs tests in that desktop.
param(
    [Parameter(Mandatory)][string]$UserName,
    [string]$GuestRoot = 'C:\WinScreenTest',
    [string]$TaskName = 'WinScreenTests'
)

$ErrorActionPreference = 'Stop'

# Never sleep, blank the screen or ask for a password on wake.
foreach ($setting in 'monitor-timeout-ac', 'standby-timeout-ac', 'hibernate-timeout-ac', 'disk-timeout-ac') {
    powercfg /change $setting 0 | Out-Null
}
powercfg /hibernate off | Out-Null
powercfg /setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK 0 | Out-Null
powercfg /setactive SCHEME_CURRENT | Out-Null

$policies = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Set-ItemProperty $policies -Name InactivityTimeoutSecs -Value 0 -Type DWord
Set-ItemProperty $policies -Name EnableFirstLogonAnimation -Value 0 -Type DWord
$personalization = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization'
New-Item $personalization -Force | Out-Null
Set-ItemProperty $personalization -Name NoLockScreen -Value 1 -Type DWord

# Screen saver off for the test account (its hive is loaded while it is signed in).
$sid = (New-Object Security.Principal.NTAccount($env:COMPUTERNAME, $UserName)).Translate([Security.Principal.SecurityIdentifier]).Value
$desktop = "Registry::HKEY_USERS\$sid\Control Panel\Desktop"
if (Test-Path $desktop) {
    Set-ItemProperty $desktop -Name ScreenSaveActive -Value '0'
    Set-ItemProperty $desktop -Name ScreenSaverIsSecure -Value '0'
}
# No tips and toasts that steal focus during desktop tests.
$notifications = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\PushNotifications"
if (-not (Test-Path $notifications)) { New-Item $notifications -Force | Out-Null }
Set-ItemProperty $notifications -Name ToastEnabled -Value 0 -Type DWord
$content = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"
if (Test-Path $content) {
    foreach ($value in 'SubscribedContentEnabled', 'SystemPaneSuggestionsEnabled', 'SoftLandingEnabled', 'SilentInstalledAppsEnabled') {
        Set-ItemProperty $content -Name $value -Value 0 -Type DWord
    }
}

# Updates can't restart the machine during a run.
foreach ($service in 'wuauserv', 'UsoSvc', 'WaaSMedicSvc') {
    try { Stop-Service $service -Force -ErrorAction Stop } catch { }
    try { Set-Service $service -StartupType Disabled -ErrorAction Stop } catch { }
}

try { Add-MpPreference -ExclusionPath $GuestRoot -ErrorAction Stop } catch { }

# The answer file held the account password in plain text.
foreach ($file in 'C:\Windows\Panther\unattend.xml', 'C:\Windows\Panther\Unattend\unattend.xml') {
    if (Test-Path $file) { Remove-Item $file -Force }
}

# Tests must run in the signed-in console desktop, not in this remote session,
# so they start through a task that only runs while the account is signed in.
$runner = Join-Path $GuestRoot 'guest\Run-Tests.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File `"$runner`""
$principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$UserName" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null

New-Item -ItemType Directory -Path (Join-Path $GuestRoot 'run') -Force | Out-Null
Write-Output "Guest prepared for $UserName."
