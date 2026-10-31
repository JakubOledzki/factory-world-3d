# Copies the conifers we use from Epic's Project Titan sample (Fab, free; licensed for use in Unreal
# Engine projects, not redistributable) into this project, local only:
#   powershell -ExecutionPolicy Bypass -File Tools\migrate_titan.ps1 -Titan D:\Titan\ProjectTitan
# 1. runs Tools\titan_dependencies.py inside Titan (read-only: the editor only adds Saved/Intermediate/DDC there)
#    to list the tree meshes and every package they need;
# 2. copies exactly those packages to the same paths under Content\ (asset references are path-based);
# 3. checks nothing in the copied set points at a package that is missing.
# The copied folders are git-ignored and cooked via DirectoriesToAlwaysCook (Config\DefaultGame.ini).
param(
    [string]$Titan = "D:\Titan\ProjectTitan",
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8"
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$List = Join-Path $env:TEMP "titan_dependencies.txt"
$Cmd = Join-Path $Engine "Engine\Binaries\Win64\UnrealEditor-Cmd.exe"
$TitanProject = Get-ChildItem $Titan -Filter *.uproject | Select-Object -First 1

$env:FW_OUT = $List
& $Cmd $TitanProject.FullName -run=pythonscript -script="$Root\Tools\titan_dependencies.py" -unattended -nosplash -nullrhi | Out-Null
$Packages = Get-Content $List
Write-Host "Packages to copy: $($Packages.Count)"

$Copied = 0
foreach ($Package in $Packages) {
    $Relative = $Package.Substring("/Game/".Length).Replace("/", "\")
    $Source = Join-Path $Titan "Content\$Relative"
    $Target = Join-Path $Root "Content\$Relative"
    New-Item -ItemType Directory -Force (Split-Path $Target) | Out-Null
    $Files = Get-ChildItem "$Source.*" -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".uasset", ".uexp", ".ubulk", ".uptnl", ".umap" }
    if (-not $Files) { Write-Warning "missing in Titan: $Package"; continue }
    foreach ($File in $Files) {
        Copy-Item $File.FullName (Join-Path (Split-Path $Target) $File.Name) -Force
    }
    $Copied++
}
Write-Host "Copied $Copied packages into Content\ (Environment\..., Landscape\...)"

# The trees' far-distance impostor cards use materials from Titan's content-only EasyImpostor plugin
Copy-Item (Join-Path $Titan "Plugins\EasyImpostor") (Join-Path $Root "Plugins") -Recurse -Force
Write-Host "Copied the EasyImpostor plugin into Plugins\"
