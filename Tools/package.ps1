# Builds, cooks and packages the game for Windows into D:\Factory-World\Builds\<Configuration>.
#   powershell -ExecutionPolicy Bypass -File Tools\package.ps1                 (Shipping)
#   powershell -ExecutionPolicy Bypass -File Tools\package.ps1 -Configuration Development
# Run Tools\setup_assets.ps1 first: the photographed art (Content/External, Content/ThirdParty) is not in
# git and is cooked from what is on this machine. Cook warnings are collected in <output>\CookWarnings.txt.
param(
    [ValidateSet("Development", "Shipping")]
    [string]$Configuration = "Shipping",
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8",
    [string]$Output = "D:\Factory-World\Builds"
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "FactoryWorld3D.uproject"
$Target = Join-Path $Output $Configuration

if (-not (Test-Path (Join-Path $Root "Content\External"))) {
    Write-Warning "Content\External is missing: the package will only have placeholder art. Run Tools\setup_assets.ps1 first."
}

$Log = Join-Path $env:TEMP "FactoryWorld3D-package-$Configuration.log"
$Arguments = @("BuildCookRun", "-project=$Project", "-noP4", "-platform=Win64", "-clientconfig=$Configuration",
    "-build", "-cook", "-stage", "-pak", "-iostore", "-package", "-archive", "-archivedirectory=$Target",
    "-utf8output", "-unattended", "-nocompileeditor")
$Lines = & (Join-Path $Engine "Engine\Build\BatchFiles\RunUAT.bat") @Arguments 2>&1
$Exit = $LASTEXITCODE
$Lines | Set-Content $Log -Encoding utf8
$Lines | Select-String -Pattern "BUILD SUCCESSFUL|AutomationTool exiting|Error:" | ForEach-Object { $_.Line }

New-Item -ItemType Directory -Force $Target | Out-Null
Select-String -Path $Log -Pattern "LogCook: Warning|LogCook: Error|Warning: .*\.uasset" | ForEach-Object { $_.Line } |
    Out-File (Join-Path $Target "CookWarnings.txt") -Encoding utf8
$Warnings = (Get-Content (Join-Path $Target "CookWarnings.txt") | Measure-Object -Line).Lines
Write-Host "Cook warnings: $Warnings (see $Target\CookWarnings.txt)"
if ($Exit -ne 0) { throw "Packaging failed ($Exit), log: $Log" }

$Exe = Get-ChildItem $Target -Recurse -Filter "FactoryWorld3D*.exe" | Select-Object -First 1
Write-Host "Game: $($Exe.FullName)"
Write-Host "Server address: add -ServerUrl=ws://host:5080/ws to the command line, or ServerUrl in Saved\Config\Windows\Game.ini"
