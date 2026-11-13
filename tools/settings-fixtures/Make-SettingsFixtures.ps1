# Writes tests\WinScreen.Tests\Fixtures\settings\<version>.json for each release tag (or the ones named), as
# that version itself saves settings: each tag is extracted with git archive into a temporary folder, this
# folder's generator is built against that tag's code there, and it saves made-up choices with the tag's own
# AppSettings.Save. Nothing from a real person's settings is used.
#
#   .\tools\settings-fixtures\Make-SettingsFixtures.ps1                 # every v* tag
#   .\tools\settings-fixtures\Make-SettingsFixtures.ps1 -Tag v0.11.0    # one new release
#
# The generator's project reference only resolves inside the extracted copy; don't build it in place.
param([string[]]$Tag)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'tests\WinScreen.Tests\Fixtures\settings'
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not $env:WINSCREEN_ZIG) { $env:WINSCREEN_ZIG = "$repo\.local\hover-tools\zig-windows-x86_64-0.13.0\zig.exe" }
if (-not $Tag) { $Tag = git -C $repo tag -l 'v*' --sort=v:refname }
$work = Join-Path ([IO.Path]::GetTempPath()) "winscreen-settings-fixtures-$PID"

try {
    foreach ($name in $Tag) {
        $tree = Join-Path $work $name
        New-Item -ItemType Directory -Force $tree | Out-Null
        $tar = "$tree.tar"
        git -C $repo archive --format=tar -o $tar $name
        if ($LASTEXITCODE -ne 0) { throw "No tag $name." }
        tar -xf $tar -C $tree
        $generator = Join-Path $tree 'fixture-gen'
        New-Item -ItemType Directory -Force $generator | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'FixtureGen.csproj') $generator

        # Built inside the copy, so that tag's global.json, Directory.Build.props and packages apply.
        Push-Location $tree
        try {
            & $dotnet build fixture-gen\FixtureGen.csproj -c Release -v q -nologo -o gen-out
            if ($LASTEXITCODE -ne 0) { throw "$name did not build." }
            & .\gen-out\FixtureGen.exe (Join-Path $out "$($name.TrimStart('v')).json")
            if ($LASTEXITCODE -ne 0) { throw "The generator failed for $name." }
        }
        finally { Pop-Location }
    }
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
