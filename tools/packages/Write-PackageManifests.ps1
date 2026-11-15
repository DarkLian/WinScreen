<#
.SYNOPSIS
  Writes the winget and Scoop manifests for a published stable release, from the release's own files.

.DESCRIPTION
  Downloads the release's installer and portable zip from GitHub (the files the release workflow built and
  published, not a local build), takes their SHA-256, and writes:
  - dist\packages\winget\manifests\d\DarkLian\WinScreen\<version>\ : the three files a winget-pkgs pull request
    adds (version, installer, default locale);
  - bucket\winscreen.json : the Scoop manifest, for this repository used as a bucket
    (scoop bucket add winscreen https://github.com/DarkLian/WinScreen).
  Nothing is submitted or committed: submitting to winget and committing the bucket are the owner's steps.

.EXAMPLE
  .\tools\packages\Write-PackageManifests.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Repository = 'DarkLian/WinScreen',
    # Already downloaded files, instead of downloading them (for a check without network).
    [string]$Folder
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$templates = Join-Path $PSScriptRoot 'templates'
$base = "https://github.com/$Repository/releases/download/v$Version"
$setupName = "WinScreen-$Version-release-x64-Setup.exe"
$zipName = "WinScreen-$Version-release-x64-Portable.zip"

if (-not $Folder) {
    $Folder = Join-Path $repo "dist\packages\download\$Version"
    New-Item -ItemType Directory -Force $Folder | Out-Null
    foreach ($name in $setupName, $zipName) {
        Invoke-WebRequest -Uri "$base/$name" -OutFile (Join-Path $Folder $name) -UseBasicParsing
    }
}
$setupHash = (Get-FileHash -Algorithm SHA256 (Join-Path $Folder $setupName)).Hash
$zipHash = (Get-FileHash -Algorithm SHA256 (Join-Path $Folder $zipName)).Hash

function Fill([string]$Template, [string]$Destination) {
    $text = [IO.File]::ReadAllText((Join-Path $templates $Template))
    $text = $text.Replace('{{VERSION}}', $Version).Replace('{{SETUP_URL}}', "$base/$setupName").Replace('{{SETUP_SHA256}}', $setupHash).
        Replace('{{ZIP_URL}}', "$base/$zipName").Replace('{{ZIP_SHA256}}', $zipHash).Replace('{{REPOSITORY}}', $Repository)
    if ($text -match '\{\{[A-Z_0-9]+\}\}') { throw "A placeholder was left in $Template." }
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    [IO.File]::WriteAllText($Destination, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Output "Wrote $Destination"
}

$winget = Join-Path $repo "dist\packages\winget\manifests\d\DarkLian\WinScreen\$Version"
Fill 'DarkLian.WinScreen.yaml' (Join-Path $winget 'DarkLian.WinScreen.yaml')
Fill 'DarkLian.WinScreen.installer.yaml' (Join-Path $winget 'DarkLian.WinScreen.installer.yaml')
Fill 'DarkLian.WinScreen.locale.en-US.yaml' (Join-Path $winget 'DarkLian.WinScreen.locale.en-US.yaml')
Fill 'winscreen.json' (Join-Path $repo 'bucket\winscreen.json')
$null = Get-Content -Raw (Join-Path $repo 'bucket\winscreen.json') | ConvertFrom-Json

if (Get-Command winget -ErrorAction SilentlyContinue) {
    winget validate --manifest $winget
    if ($LASTEXITCODE -ne 0) { throw 'winget validate refused the manifests.' }
}
else { Write-Output 'winget is not on this PC; the manifests were not validated.' }
