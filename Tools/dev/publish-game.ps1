# Publishes the game for the launcher (Launcher/): the packaged game and the launcher go to the server's
# deploy/download, which Caddy serves at https://<domain>/download/ (deploy/README.md, "Launcher").
#   powershell -ExecutionPolicy Bypass -File Tools\dev\publish-game.ps1 -Build
#   powershell -ExecutionPolicy Bypass -File Tools\dev\publish-game.ps1 -PackageDir D:\Factory-World\FactoryWorld3D-gra\FactoryWorld3D
# -Build packages the game first (Tools\dev\package.ps1); otherwise -PackageDir is a folder with FactoryWorld3D.exe.
# Every file is stored as files/<sha256>: only files the server does not have yet are uploaded, and the new
# manifest.json replaces the old one in one step, so players never see half a version.
param(
    [switch]$Build,
    [string]$PackageDir = "",
    [string]$ServerUrl = "wss://2-31-169-64.sslip.io/ws",
    [string]$BaseUrl = "https://2-31-169-64.sslip.io/download/",
    [string]$Server = "root@2.31.169.64",
    [string]$RemoteDir = "/opt/factoryworld3d/deploy/download",
    [string]$Key = "$env:USERPROFILE\.ssh\factoryworld3d_deploy",
    [string]$Version = (Get-Date -Format "yyyy.MM.dd-HHmm")
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$Ssh = @("-i", $Key, "-o", "BatchMode=yes")

if ($Build) {
    $out = Join-Path $Root "Saved\Package"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "package.ps1") -ServerUrl $ServerUrl -Output $out
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed" }
    $PackageDir = Join-Path $out "Windows"
}
if (-not $PackageDir -or -not (Test-Path (Join-Path $PackageDir "FactoryWorld3D.exe"))) {
    throw "No packaged game: -Build, or -PackageDir with FactoryWorld3D.exe in it"
}
$PackageDir = (Resolve-Path $PackageDir).Path

Write-Host "== Launcher"
dotnet publish (Join-Path $Root "Launcher") -c Release "-p:LauncherBaseUrl=$BaseUrl" "-p:GameServerUrl=$ServerUrl" | Select-String -Pattern "error" | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed" }
$launcher = Join-Path $Root "Launcher\bin\Release\net10.0-windows\win-x64\publish\FactoryWorld3D-Launcher.exe"
$launcherVersion = (Get-Item $launcher).VersionInfo.FileVersion

Write-Host "== Manifest"
$files = Get-ChildItem $PackageDir -Recurse -File | Where-Object { $_.Name -notlike "Manifest_*" } | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($PackageDir.Length + 1).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        local = $_.FullName
    }
}
$launcherHash = (Get-FileHash $launcher -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = $Version
    exe = "FactoryWorld3D.exe"
    files = @($files | ForEach-Object { [ordered]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
    launcher = [ordered]@{ version = $launcherVersion; size = (Get-Item $launcher).Length; sha256 = $launcherHash }
}
$manifestFile = Join-Path $env:TEMP "factoryworld3d-manifest.json"
[IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 5))
Write-Host "$($files.Count) files, $([math]::Round(($files | Measure-Object -Property size -Sum).Sum / 1MB)) MB, version $Version, launcher $launcherVersion"

Write-Host "== Upload"
& ssh @Ssh $Server "mkdir -p $RemoteDir/files && ls $RemoteDir/files" | Set-Variable have
$have = @($have)
$uploads = @($files | ForEach-Object { @{ sha = $_.sha256; local = $_.local; size = $_.size } }) + @(@{ sha = $launcherHash; local = $launcher; size = (Get-Item $launcher).Length })
$new = $uploads | Where-Object { $have -notcontains $_.sha } | Sort-Object { $_.sha } -Unique
$i = 0
foreach ($file in $new) {
    $i++
    Write-Host ("[{0}/{1}] {2} ({3} MB)" -f $i, $new.Count, (Split-Path $file.local -Leaf), [math]::Round($file.size / 1MB, 1))
    & scp @Ssh $file.local "${Server}:$RemoteDir/files/$($file.sha).part"
    if ($LASTEXITCODE -ne 0) { throw "Upload of $($file.local) failed" }
    & ssh @Ssh $Server "mv $RemoteDir/files/$($file.sha).part $RemoteDir/files/$($file.sha)"
}
Write-Host "$($new.Count) new files uploaded, $($uploads.Count - $new.Count) already there"

# The launcher at a fixed address for new players, then the manifest last: the new version appears at once
& scp @Ssh $launcher "${Server}:$RemoteDir/FactoryWorld3D-Launcher.exe.part"
& scp @Ssh $manifestFile "${Server}:$RemoteDir/manifest.json.part"
& ssh @Ssh $Server "chmod -R a+rX $RemoteDir && mv $RemoteDir/FactoryWorld3D-Launcher.exe.part $RemoteDir/FactoryWorld3D-Launcher.exe && mv $RemoteDir/manifest.json.part $RemoteDir/manifest.json"
if ($LASTEXITCODE -ne 0) { throw "Swapping the manifest failed" }
Write-Host "Published version ${Version}: ${BaseUrl}FactoryWorld3D-Launcher.exe"
