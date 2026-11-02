# Builds the game client (UE editor target, the one -game runs) and/or the 3D game's server.
#   powershell -ExecutionPolicy Bypass -File Tools\dev\build.ps1            # both
#   powershell -ExecutionPolicy Bypass -File Tools\dev\build.ps1 -Client    # only the client
#   powershell -ExecutionPolicy Bypass -File Tools\dev\build.ps1 -Server    # only the server (Release, so a running
#                                                                           # Debug server does not lock its files)
# Exit code 0 when everything built.
param(
    [switch]$Client,
    [switch]$Server,
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8",
    [string]$ServerConfiguration = "Release"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if (-not $Client -and -not $Server) { $Client = $true; $Server = $true }
$failed = $false

if ($Client) {
    Write-Host "== Client (FactoryWorld3DEditor Win64 Development)"
    & "$Engine\Engine\Build\BatchFiles\Build.bat" FactoryWorld3DEditor Win64 Development "-Project=$Root\FactoryWorld3D.uproject" -WaitMutex -NoHotReload |
        Select-String -Pattern "error|Result:" | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "Client build FAILED" }
}

if ($Server) {
    Write-Host "== Server ($ServerConfiguration)"
    dotnet build "$Root\Server\FactoryWorld.slnx" -c $ServerConfiguration -v q | Select-String -Pattern " error |Errors|Błędy" | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "Server build FAILED" }
}

if ($failed) { exit 1 }
Write-Host "Built."
