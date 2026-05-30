# bump-version.ps1 — updates Version in all .csproj files
# Usage: .\scripts\bump-version.ps1 -Version 0.5.0
param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

# Strip leading 'v' in case tag is passed directly (v0.5.0 -> 0.5.0)
$Version = $Version.TrimStart('v')

# AssemblyVersion and FileVersion only accept Major.Minor.Build[.Revision]
# Strip any pre-release suffix (e.g. 0.5.0-rc.1 -> 0.5.0)
$assemblyVersion = ($Version -split '-')[0]

$csprojFiles = Get-ChildItem -Recurse -Filter '*.csproj' -Exclude 'TunnelAgent.Tests.csproj' |
    Where-Object { -not $_.PSIsContainer -and $_.FullName -notmatch '\\obj\\|\\bin\\' }

foreach ($file in $csprojFiles) {
    $content = Get-Content $file.FullName -Raw

    $updated = $content `
        -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" `
        -replace '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$assemblyVersion.0</AssemblyVersion>" `
        -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$assemblyVersion.0</FileVersion>"

    if ($updated -ne $content) {
        Set-Content $file.FullName $updated -NoNewline
        Write-Host "  Bumped $($file.Name) -> $Version"
    }
}

Write-Host "Version bumped to $Version"
