param(
    [string]$FfmpegPath = '',
    [string]$FfprobePath = ''
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not $FfmpegPath) { $FfmpegPath = (Get-Command ffmpeg -ErrorAction Stop).Source }
if (-not $FfprobePath) { $FfprobePath = (Get-Command ffprobe -ErrorAction Stop).Source }
$destination = Join-Path $PSScriptRoot 'artifacts/portable'
dotnet publish VidCropper.csproj -c Release -r win-x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$toolsDirectory = Join-Path $destination 'tools'
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $toolsDirectory 'ffmpeg.exe')
Copy-Item -LiteralPath $FfprobePath -Destination (Join-Path $toolsDirectory 'ffprobe.exe')
$ffmpegRoot = Split-Path (Split-Path $FfmpegPath)
foreach ($name in @('LICENSE', 'README.txt')) {
    $source = Join-Path $ffmpegRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $toolsDirectory $name) }
}
@'
@echo off
cd /d "%~dp0"
if "%~1"=="" (
  VidCropper.exe --OpenBrowser=true
) else (
  VidCropper.exe --OpenBrowser=true --port "%~1"
)
pause
'@ | Set-Content -LiteralPath (Join-Path $destination 'Start.cmd') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination
Write-Host "Ready: $destination"
