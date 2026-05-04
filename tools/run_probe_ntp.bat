@echo off
cd /d "%~dp0"
python probe_ntp_server.py
echo.
echo Log: %CD%\probe_ntp_output.log
pause
