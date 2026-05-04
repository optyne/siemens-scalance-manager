@echo off
setlocal
cd /d "%~dp0"
python probe_vpn.py
pause >nul
