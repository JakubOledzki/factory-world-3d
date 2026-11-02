# Packages the game for players (Win64, Shipping) with the address of the game's server built in.
#   powershell -ExecutionPolicy Bypass -File Tools\dev\package.ps1 -ServerUrl wss://gra.example.com/ws
#   powershell -ExecutionPolicy Bypass -File Tools\dev\package.ps1 -ServerUrl wss://gra.example.com/ws -Output D:\FactoryWorld3D-paczka
# The address goes into Config/DefaultGame.ini only for the packaging and is put back after it (the repository keeps
# ws://localhost:5081/ws for development). A packaged game also takes -ServerUrl=... on its command line.
# Output: <Output>\Windows\FactoryWorld3D.exe and the rest of the game, ready to zip.
param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [string]$Output = "",
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $Output) { $Output = Join-Path $Root "Saved\Package" }
if ($ServerUrl -notmatch '^wss?://') { throw "ServerUrl must start with wss:// (or ws:// for a test)" }

$Ini = Join-Path $Root "Config\DefaultGame.ini"
$Original = [IO.File]::ReadAllText($Ini)
$Pattern = '(?m)^ServerUrl=.*$'
if ($Original -notmatch $Pattern) { throw "No ServerUrl= line in $Ini" }
try {
    [IO.File]::WriteAllText($Ini, ($Original -replace $Pattern, "ServerUrl=`"$ServerUrl`""))
    & "$Engine\Engine\Build\BatchFiles\RunUAT.bat" BuildCookRun "-project=$Root\FactoryWorld3D.uproject" `
        -platform=Win64 -clientconfig=Shipping -build -cook -stage -pak -prereqs -archive "-archivedirectory=$Output" `
        -nocompileeditor -utf8output -unattended
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed (RunUAT exit code $LASTEXITCODE)" }
}
finally {
    [IO.File]::WriteAllText($Ini, $Original)
}
Write-Host "Packaged for $ServerUrl in $Output"
