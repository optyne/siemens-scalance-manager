@echo off
cd /d "%~dp0"
python probe_snmp_toggle.py
echo.
echo Log: %CD%\probe_snmp_output.log
pause
