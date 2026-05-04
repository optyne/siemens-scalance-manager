@echo off
setlocal
cd /d "%~dp0SmokeTest"

echo === Scalance SmokeTest ===
echo Running: dotnet run --project SmokeTest.csproj
echo.

dotnet run --project SmokeTest.csproj %*
set rc=%errorlevel%

echo.
echo === Done. Exit code: %rc% ===
echo Log: %~dp0SmokeTest\smoke.log
pause >nul
exit /b %rc%
