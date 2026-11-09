param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
New-Item (Split-Path $Output) -ItemType Directory -Force | Out-Null
$source = Join-Path $PSScriptRoot 'Hover.c'
if ($env:WINSCREEN_ZIG) {
    & $env:WINSCREEN_ZIG cc -target x86_64-windows-gnu -shared -O2 -Wall -Wextra -Werror $source -o $Output -luser32
} else {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (!(Test-Path $vswhere)) {
        if ($env:GITHUB_ACTIONS) { throw 'MSVC C++ build tools are required in CI.' }
        if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output }
        Write-Warning 'Native hover helper unavailable: install MSVC C++ build tools.'; exit 0
    }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) {
        if ($env:GITHUB_ACTIONS) { throw 'MSVC C++ build tools are required in CI.' }
        if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output }
        Write-Warning 'Native hover helper unavailable: install MSVC C++ build tools.'; exit 0
    }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $obj = [IO.Path]::ChangeExtension($Output, '.obj')
    & cmd /d /s /c "`"`"$vcvars`" >nul && cl /nologo /std:c11 /LD /O2 /W4 /WX `"$source`" /Fo`"$obj`" /link /OUT:`"$Output`" user32.lib`""
}
if ($LASTEXITCODE) { throw 'Native hover helper build failed' }
