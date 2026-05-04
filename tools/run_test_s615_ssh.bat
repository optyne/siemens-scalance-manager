@echo off
setlocal
cd /d "%~dp0"

echo === SCALANCE S615 SSH probe ===
echo Working dir: %CD%
echo.

where python >nul 2>&1
if errorlevel 1 (
    echo Python not found in PATH. Install Python 3.x or check PATH.
    pause
    exit /b 1
)

python test_s615_ssh.py
set rc=%errorlevel%

echo.
echo === Done. Exit code: %rc% ===
echo Log written to test_s615_ssh_output.log
echo (Press any key to close)
pause >nul
exit /b %rc%
