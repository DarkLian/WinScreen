param([Parameter(Mandatory=$true)][string]$Zig, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
& $Zig cc -target x86_64-windows-gnu -shared -O2 -Wall -Wextra -Werror (Join-Path $PSScriptRoot 'HoverProbe.c') -o $Output -luser32
if ($LASTEXITCODE) { throw 'Native hover probe build failed' }
