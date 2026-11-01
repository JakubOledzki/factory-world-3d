# One command to make the photographed art in Content/External (not in git):
#   powershell -ExecutionPolicy Bypass -File Tools\setup_assets.ps1
# 1. downloads the CC0 models and textures from Poly Haven (checksummed) and the Copernicus DEM tiles,
# 2. prepares them in Blender (decimation, terrain, real elevation around the demo plot),
# 3. rebuilds the materials and imports the art into Unreal (Nanite, texture settings, material usages).
# Runs again safely: finished steps are skipped. Needs Node.js, Blender 5.2 and Unreal Engine 5.8.
param(
    [string]$Blender = "D:\Programs\Blender\blender.exe",
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8",
    # Plots to cut real elevation for (world grid x,y)
    [string[]]$DemPlots = @("3852,357")
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "== Poly Haven (CC0)"
node Tools/fetch_polyhaven.mjs
if ($LASTEXITCODE -ne 0) { throw "download failed" }

Write-Host "== ambientCG decals (CC0)"
New-Item -ItemType Directory -Force RawAssets\ambientcg | Out-Null
foreach ($id in @("Leaking007", "Leaking008", "Leaking009")) {
    if (-not (Test-Path "RawAssets\ambientcg\$id")) {
        Invoke-WebRequest "https://ambientcg.com/get?file=$($id)_2K-PNG.zip" -OutFile "RawAssets\ambientcg\$id.zip"
        Expand-Archive "RawAssets\ambientcg\$id.zip" "RawAssets\ambientcg\$id"
        Remove-Item "RawAssets\ambientcg\$id.zip"
    }
}

Write-Host "== Copernicus GLO-30 DEM"
New-Item -ItemType Directory -Force RawAssets\dem | Out-Null
if (-not (Test-Path RawAssets\node_modules\geotiff)) {
    npm install --prefix RawAssets --silent --no-audit --no-fund geotiff@2.1.3
}
foreach ($tile in @("N49_00_E019_00", "N49_00_E020_00")) {
    $name = "Copernicus_DSM_COG_10_${tile}_DEM"
    $file = "RawAssets\dem\$name.tif"
    if (-not (Test-Path $file)) {
        Invoke-WebRequest "https://copernicus-dem-30m.s3.amazonaws.com/$name/$name.tif" -OutFile $file
    }
}
New-Item -ItemType Directory -Force RawAssets\processed | Out-Null
foreach ($plot in $DemPlots) {
    $x, $y = $plot.Split(",")
    if (-not (Test-Path "RawAssets\processed\dem_${x}_${y}.bin")) { node Tools/dem_extract.mjs $x $y }
}

Write-Host "== Blender"
if (-not (Test-Path RawAssets\processed\pine_tree_01.glb)) {
    & $Blender --background --factory-startup --python Tools\blender\prepare_polyhaven.py
}
if (-not (Test-Path RawAssets\processed\factory\belt_straight.glb)) {
    & $Blender --background --factory-startup --python Tools\blender\make_factory.py
}
if (-not (Test-Path RawAssets\processed\factory\tool_pickaxe.glb)) {
    & $Blender --background --factory-startup --python Tools\blender\make_tools.py
}
if (-not (Test-Path RawAssets\processed\icons\icon_tool_pickaxe.png)) {
    & $Blender --background --factory-startup --python Tools\blender\render_icons.py
}
if (-not (Test-Path RawAssets\processed\terrain_mountains.glb)) {
    & $Blender --background --factory-startup --python Tools\blender\make_terrain.py
}
if (-not (Test-Path RawAssets\processed\mugo\mugo_pine_01.glb)) {
    & $Blender --background --factory-startup --python Tools\blender\make_mugo_pine.py
}
if (-not (Test-Path RawAssets\processed\crops\crop_wheat_ripe.glb)) {
    & $Blender --background --factory-startup --python Tools\blender\make_crops.py
}
if (-not (Test-Path RawAssets\processed\audio\amb_wind_loop.wav)) {
    & $Blender --background --factory-startup --python Tools\make_audio.py
}

Write-Host "== Unreal"
$Cmd = Join-Path $Engine "Engine\Binaries\Win64\UnrealEditor-Cmd.exe"
$Project = Join-Path $Root "FactoryWorld3D.uproject"
# Materials first: the import re-parents the scans onto M_FWScan, which make_materials.py rebuilds
& $Cmd $Project -run=pythonscript -script="$Root\Tools\make_materials.py" -unattended -nosplash | Out-Null
# Character hair and clothes, built on the mannequin (Tools/copy_mannequins.ps1): export it from the editor
# (the FBX exporter needs a renderer, so the full editor runs and closes itself), then build them in Blender
if ((Test-Path "Content\Characters\Mannequins\Meshes\SKM_Manny_Simple.uasset") -and -not (Test-Path RawAssets\processed\character\shoes.fbx)) {
    $Editor = Join-Path $Engine "Engine\Binaries\Win64\UnrealEditor.exe"
    Start-Process $Editor -ArgumentList "`"$Project`"", "-ExecutePythonScript=`"$Root\Tools\export_mannequin.py`"", "-nosplash", "-unattended" -Wait
    & $Blender --background --factory-startup --python Tools\blender\make_character_gear.py
}
& $Cmd $Project -run=pythonscript -script="$Root\Tools\import_external.py" -unattended -nosplash | Out-Null
Write-Host "Done: Content/External is ready."
