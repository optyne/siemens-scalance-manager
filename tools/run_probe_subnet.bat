@echo off
cd /d "%~dp0"
python probe_subnet_alias_tia.py
echo.
echo Log: %CD%\probe_subnet_output.log
pause
