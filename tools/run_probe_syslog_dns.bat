@echo off
cd /d "%~dp0"
python probe_syslog_dns.py
echo.
echo Log: %CD%\probe_syslog_dns_output.log
pause
