# Builds, cooks and packages the game for Windows into D:\Factory-World\Builds\<Configuration>.
#   powershell -ExecutionPolicy Bypass -File Tools\package.ps1                 (Shipping)
#   powershell -ExecutionPolicy Bypass -File Tools\package.ps1 -Configuration Development
# Run Tools\setup_assets.ps1 first: the photographed art (Content/External, Content/ThirdParty) is not in
# git and is cooked from what is on this machine. Cook warnings are collected in <output>\CookWarnings.txt.
param(
    [ValidateSet("Development", "Shipping")]
    [string]$Configuration = "Shipping",
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8",
    [string]$Output = "D:\Factory-World\Builds",
    # Use when the editor is open (its DLLs are locked); otherwise the editor target is rebuilt so the
    # cook never runs with an editor DLL older than the C++ sources
    [switch]$SkipEditorBuild,
    # For testing this script: run another batch file instead of RunUAT.bat
    [string]$RunUAT = ""
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
    "-utf8output", "-unattended")
if ($SkipEditorBuild) { $Arguments += "-nocompileeditor" }
if (-not $RunUAT) { $RunUAT = Join-Path $Engine "Engine\Build\BatchFiles\RunUAT.bat" }

# Windows PowerShell turns a native command's stderr lines into errors, which "Stop" would make
# terminating before the log is written: let them through, and stream every line to the screen and the log
$ErrorActionPreference = "Continue"
& $RunUAT @Arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $Log
$Exit = $LASTEXITCODE
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force $Target | Out-Null
$Warnings = @(Select-String -Path $Log -Pattern "LogCook: Warning|LogCook: Error" | ForEach-Object { $_.Line })
$Warnings | Out-File (Join-Path $Target "CookWarnings.txt") -Encoding utf8
Write-Host "Cook warnings: $($Warnings.Count) (see $Target\CookWarnings.txt)"
if ($Exit -ne 0) { throw "Packaging failed ($Exit), log: $Log" }

$Exe = Get-ChildItem $Target -Recurse -Filter "FactoryWorld3D*.exe" | Select-Object -First 1
Write-Host "Game: $($Exe.FullName)"
Write-Host "Server address: add -ServerUrl=ws://host:5080/ws to the command line, or ServerUrl in Saved\Config\Windows\Game.ini"
