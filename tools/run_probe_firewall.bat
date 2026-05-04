@echo off
chcp 65001 > nul
cd /d "%~dp0"
python probe_firewall_ipv4rule.py
echo.
echo ================================================================
echo Log: %CD%\probe_firewall_output.log
echo ================================================================
pause
