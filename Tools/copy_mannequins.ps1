# Copies the UE5 mannequin (Manny) from the engine's template content into Content\Characters\Mannequins,
# for the character creator's preview. Every Unreal Engine 5 install has it (Templates\TemplateResources);
# it is Epic content for use in Unreal Engine projects, so it stays out of git like Project Titan.
#   powershell -ExecutionPolicy Bypass -File Tools\copy_mannequins.ps1
# Only what the preview needs: meshes, materials, textures, rigs (the mesh refers to them) and the idle animation.
param(
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8"
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $Engine "Templates\TemplateResources\High\Characters\Content\Mannequins"
$Target = Join-Path $Root "Content\Characters\Mannequins"
if (-not (Test-Path $Source)) { throw "Mannequin template content not found: $Source" }

foreach ($Folder in @("Meshes", "Materials", "Textures")) {
    New-Item -ItemType Directory -Force (Join-Path $Target $Folder) | Out-Null
    Copy-Item (Join-Path $Source "$Folder\*") (Join-Path $Target $Folder) -Recurse -Force
}
New-Item -ItemType Directory -Force (Join-Path $Target "Rigs"), (Join-Path $Target "Anims\Unarmed") | Out-Null
Copy-Item (Join-Path $Source "Rigs\*") (Join-Path $Target "Rigs") -Force
Copy-Item (Join-Path $Source "Anims\Unarmed\MM_Idle.uasset") (Join-Path $Target "Anims\Unarmed") -Force
# The full body that casts the player's shadow walks, runs and falls
foreach ($Anim in @("Walk\MF_Unarmed_Walk_Fwd", "Jog\MF_Unarmed_Jog_Fwd", "Jump\MM_Fall_Loop")) {
    $To = Join-Path $Target "Anims\Unarmed\$Anim.uasset"
    New-Item -ItemType Directory -Force (Split-Path $To) | Out-Null
    Copy-Item (Join-Path $Source "Anims\Unarmed\$Anim.uasset") $To -Force
}
Write-Host "Copied the mannequin into $Target"
