param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts/release/portable' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) {
    throw 'Output folder must be empty. Choose a new -OutputDirectory to avoid including stale binaries.'
}
dotnet publish VidCropper.csproj -c Release -r win-x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$runtimeConfig = Get-Content -LiteralPath (Join-Path $destination 'VidCropper.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $packageId = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $package = $packageRoots | ForEach-Object { Join-Path $_ "$packageId/$($framework.version)" } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $package) { throw "Cannot find runtime package notices for $packageId." }
    $noticeFolder = Join-Path $destination "licenses/$($framework.name)"
    New-Item -ItemType Directory -Force -Path $noticeFolder | Out-Null
    Copy-Item -LiteralPath (Join-Path $package 'LICENSE.txt') -Destination $noticeFolder
    Copy-Item -LiteralPath (Join-Path $package 'THIRD-PARTY-NOTICES.TXT') -Destination $noticeFolder
}
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $destination
}
@'
@echo off
cd /d "%~dp0"
if "%~1"=="" (
  VidCropper.exe --OpenBrowser=true --InstallTools=true
) else (
  VidCropper.exe --OpenBrowser=true --InstallTools=true --port "%~1"
)
pause
'@ | Set-Content -LiteralPath (Join-Path $destination 'Start.cmd') -Encoding ascii
Write-Host "Ready: $destination"
