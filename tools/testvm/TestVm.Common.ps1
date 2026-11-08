# Shared helpers for New-TestVm.ps1 and Invoke-TestVmRun.ps1. Dot-source it; it does nothing on its own.

Set-StrictMode -Version 3.0

# Not under %LOCALAPPDATA%\WinScreen: that is the installed app's folder, and uninstalling removes it.
$script:TestVmStateRoot = Join-Path $env:LOCALAPPDATA 'WinScreenTestVm'
$script:GuestRoot = 'C:\WinScreenTest'
$script:GuestTaskName = 'WinScreenTests'
$script:EvaluationWarningDays = 80

function Get-TestVmStateFolder([string]$Name) {
    $folder = Join-Path $script:TestVmStateRoot $Name
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    $folder
}

function Get-TestVmCredential([string]$Name) {
    $file = Join-Path (Get-TestVmStateFolder $Name) 'credential.xml'
    if (-not (Test-Path -LiteralPath $file)) { throw "No saved sign-in for VM '$Name'. Create it with New-TestVm.ps1 first." }
    Import-Clixml -LiteralPath $file
}

function Assert-HyperVAccess {
    if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
        throw 'The Hyper-V PowerShell module is missing. Turn on Hyper-V in Windows Features and restart.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $isHyperVAdmin = $principal.IsInRole([Security.Principal.SecurityIdentifier]::new('S-1-5-32-578'))
    if (-not ($isAdmin -or $isHyperVAdmin)) {
        throw 'Run this from an elevated PowerShell (Run as administrator), or as a member of Hyper-V Administrators.'
    }
    Import-Module Hyper-V -ErrorAction Stop
}

function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][string]$What,
        [int]$PollSeconds = 5
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $result = $null
        try { $result = & $Condition } catch { $result = $null }
        if ($result) { return $result }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out after $TimeoutSeconds seconds waiting for $What." }
        Start-Sleep -Seconds $PollSeconds
    }
}

# Opens a PowerShell Direct session once the guest accepts the sign-in.
function Wait-TestVmSession {
    param([string]$Name, [pscredential]$Credential, [int]$TimeoutSeconds = 600)
    Wait-Until -What "PowerShell Direct on '$Name'" -TimeoutSeconds $TimeoutSeconds -PollSeconds 10 -Condition {
        New-PSSession -VMName $Name -Credential $Credential -ErrorAction Stop
    }
}

# True once the test account's desktop is up on the console and not locked.
function Wait-TestVmDesktop {
    param([System.Management.Automation.Runspaces.PSSession]$Session, [int]$TimeoutSeconds = 600)
    Wait-Until -What 'the signed-in desktop' -TimeoutSeconds $TimeoutSeconds -PollSeconds 5 -Condition {
        Invoke-Command -Session $Session -ScriptBlock {
            $explorer = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object SessionId -gt 0)
            $locked = @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count -gt 0
            ($explorer.Count -gt 0) -and -not $locked
        }
    } | Out-Null
}

function Find-HostDotnet {
    $candidates = @($env:DOTNET_ROOT,
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
        (Join-Path $env:USERPROFILE '.dotnet'),
        (Join-Path $env:ProgramFiles 'dotnet')) | Where-Object { $_ }
    foreach ($root in $candidates) {
        if ((Test-Path (Join-Path $root 'dotnet.exe')) -and
            (Test-Path (Join-Path $root 'sdk')) -and
            (Test-Path (Join-Path $root 'shared\Microsoft.WindowsDesktop.App'))) {
            return (Resolve-Path $root).Path
        }
    }
    throw 'No .NET SDK with the Windows Desktop runtime was found. Install the .NET 10 SDK or pass -DotnetRoot.'
}

function ConvertTo-XmlText([string]$Value) { [Security.SecurityElement]::Escape($Value) }

# Answer file for an image applied straight to the disk: only the specialize and oobeSystem passes run.
function New-TestVmUnattend {
    param(
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password,
        [string]$ComputerName = 'WINSCREEN-TEST',
        [string]$Locale = 'en-US',
        [string]$TimeZone = 'UTC',
        [string]$ProductKey
    )
    $user = ConvertTo-XmlText $UserName
    $pass = ConvertTo-XmlText $Password
    $component = 'processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"'
    $keyElement = if ($ProductKey) { "      <ProductKey>$(ConvertTo-XmlText $ProductKey)</ProductKey>`n" } else { '' }
    $commands = @(
        'reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\OOBE" /v DisablePrivacyExperience /t REG_DWORD /d 1 /f',
        'reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization" /v NoLockScreen /t REG_DWORD /d 1 /f',
        'reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f',
        'reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableFirstLogonAnimation /t REG_DWORD /d 0 /f'
    )
    $order = 0
    $runSync = ($commands | ForEach-Object {
        $order++
        "        <RunSynchronousCommand wcm:action=`"add`"><Order>$order</Order><Path>$(ConvertTo-XmlText $_)</Path></RunSynchronousCommand>"
    }) -join "`n"

    @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <ComputerName>$(ConvertTo-XmlText $ComputerName)</ComputerName>
      <TimeZone>$(ConvertTo-XmlText $TimeZone)</TimeZone>
$keyElement    </component>
    <component name="Microsoft-Windows-Security-SPP-UX" $component>
      <SkipAutoActivation>true</SkipAutoActivation>
    </component>
    <component name="Microsoft-Windows-Deployment" $component>
      <RunSynchronous>
$runSync
      </RunSynchronous>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" $component>
      <InputLocale>$Locale</InputLocale>
      <SystemLocale>$Locale</SystemLocale>
      <UILanguage>$Locale</UILanguage>
      <UserLocale>$Locale</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>$user</Name>
            <DisplayName>$user</DisplayName>
            <Group>Administrators</Group>
            <Password><Value>$pass</Value><PlainText>true</PlainText></Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <LogonCount>9999999</LogonCount>
        <Username>$user</Username>
        <Password><Value>$pass</Value><PlainText>true</PlainText></Password>
      </AutoLogon>
      <TimeZone>$(ConvertTo-XmlText $TimeZone)</TimeZone>
    </component>
  </settings>
</unattend>
"@
}

function New-TestVmPassword {
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = [byte[]]::new(20)
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] }) + '!7a'
}
