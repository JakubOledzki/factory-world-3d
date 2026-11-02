# Runs the game with a test script and collects its screenshots; with -Before, also puts each next to the same shot
# from an earlier run ("Przed" | "Po") for a pull request.
#
# A test script is a text file, one console command per line after the seconds to wait before it, e.g.
#   14 FWClose
#   1 FWMarket
#   4 FWShot market
# Useful commands: FWShot <name> (screenshot), FWClose, FWMarket, FWGarage <tab>, FWInventory, FWMenu, FWMap,
# FWGoPlot <x> <y>, FWAimAt tree|rock, FWStrike 1|0, FWBuildMode, FWDevWorld (a test world, development server only),
# fw.Register <e-mail> <password> (a new local test account on the login screen). See UFUNCTION(Exec) in
# Source/FactoryWorld3D/FWPlayerController.h and Player/FWCharacter.h.
#
# The game logs in as Saved\DevLogin.ini says (Email=, Password=; a LOCAL TEST account only, never a real one, never
# in git) or shows the login screen without it. It needs the 3D server running (Start-FactoryWorld3D.bat).
#
#   powershell -ExecutionPolicy Bypass -File Tools\dev\autotest.ps1 -Script my_test.txt -Tag after
#   powershell -ExecutionPolicy Bypass -File Tools\dev\autotest.ps1 -Script my_test.txt -Tag after -Before Saved\AutoTest\before
#
# Output: Saved\AutoTest\<Tag>\*.jpg, the game's log there, and with -Before also Saved\AutoTest\<Tag>\compare\*.jpg.
param(
    [Parameter(Mandatory = $true)][string]$Script,
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Before = "",
    [int]$TimeoutSeconds = 600,
    # Another server than the .ini's, e.g. ws://localhost:5091/ws for one run from a worktree
    [string]$ServerUrl = "",
    [string]$Engine = "C:\Program Files\Epic Games\UE_5.8"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$ScriptPath = Resolve-Path $Script
$Out = Join-Path $Root "Saved\AutoTest\$Tag"
$Shots = Join-Path $Root "Saved\Screenshots"
$Log = Join-Path $Out "game.log"
New-Item -ItemType Directory -Force $Out | Out-Null
if (Test-Path $Shots) { Get-ChildItem $Shots -Filter *.png | Remove-Item }

# The last screenshot of the script tells when it is done
$last = (Get-Content $ScriptPath | Where-Object { $_ -match "FWShot\s+(\S+)" } | Select-Object -Last 1) -replace ".*FWShot\s+(\S+).*", '$1'
if (-not $last) { throw "The script takes no screenshot (FWShot <name>)." }

$gameArgs = @(
    "`"$Root\FactoryWorld3D.uproject`"", "-game", "-windowed", "-ResX=1920", "-ResY=1080", "-nosplash", "-abslog=`"$Log`"", "-FWAutoTest=`"$ScriptPath`"")
if ($ServerUrl) { $gameArgs += "-ServerUrl=$ServerUrl" }
$game = Start-Process -PassThru "$Engine\Engine\Binaries\Win64\UnrealEditor.exe" -ArgumentList $gameArgs
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not $game.HasExited) {
    if ((Test-Path $Log) -and (Select-String -Path $Log -Pattern "FWShot $last|Fatal error" -Quiet)) {
        Start-Sleep -Seconds 6  # the last screenshot is written a moment after it is asked for
        break
    }
    Start-Sleep -Seconds 3
}
if (-not $game.HasExited) { Stop-Process -Id $game.Id -Force }

# Screenshots as JPEG
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
$quality = New-Object System.Drawing.Imaging.EncoderParameters 1
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]85)
$taken = @()
if (Test-Path $Shots) {
    Get-ChildItem $Shots -Filter *.png | ForEach-Object {
        $img = [System.Drawing.Image]::FromFile($_.FullName)
        $img.Save((Join-Path $Out ($_.BaseName + ".jpg")), $codec, $quality)
        $img.Dispose()
        $taken += $_.BaseName
    }
}
Write-Host "Screenshots in $Out : $($taken -join ', ')"
if (Select-String -Path $Log -Pattern "Fatal error" -Quiet) { Write-Host "The game crashed; see $Log"; exit 1 }

# Before | after, side by side
if ($Before) {
    $BeforeDir = Resolve-Path $Before
    $Compare = Join-Path $Out "compare"
    New-Item -ItemType Directory -Force $Compare | Out-Null
    foreach ($name in $taken) {
        $a = Join-Path $BeforeDir "$name.jpg"
        if (-not (Test-Path $a)) { continue }
        $left = [System.Drawing.Image]::FromFile($a); $right = [System.Drawing.Image]::FromFile((Join-Path $Out "$name.jpg"))
        $w = 960; $h = 540
        $sheet = New-Object System.Drawing.Bitmap ($w * 2 + 12), ($h + 40)
        $g = [System.Drawing.Graphics]::FromImage($sheet)
        $g.Clear([System.Drawing.Color]::FromArgb(20, 22, 26)); $g.InterpolationMode = "HighQualityBicubic"
        $g.DrawImage($left, 0, 40, $w, $h); $g.DrawImage($right, $w + 12, 40, $w, $h)
        $font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
        $g.DrawString("Przed", $font, [System.Drawing.Brushes]::Gainsboro, 10, 6)
        $g.DrawString("Po", $font, [System.Drawing.Brushes]::Orange, $w + 22, 6)
        $sheet.Save((Join-Path $Compare "$name.jpg"), $codec, $quality)
        $left.Dispose(); $right.Dispose(); $g.Dispose(); $sheet.Dispose()
    }
    Write-Host "Comparisons in $Compare"
}
