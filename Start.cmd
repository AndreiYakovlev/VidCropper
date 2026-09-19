@echo off
cd /d "%~dp0"
if "%~1"=="" (
  dotnet run --project VidCropper.csproj --no-launch-profile -- --OpenBrowser=true
) else (
  dotnet run --project VidCropper.csproj --no-launch-profile -- --OpenBrowser=true --port "%~1"
)
pause
