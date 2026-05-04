@echo off
setlocal
cd /d "%~dp0"
python dump_running_config.py
pause >nul
