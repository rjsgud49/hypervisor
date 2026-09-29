@echo off
setlocal
cd /d "%~dp0"

where python >nul 2>&1
if errorlevel 1 (
  echo Python not found. Install Python 3 and retry.
  pause
  exit /b 1
)

python hvlab.py %*
set ERR=%ERRORLEVEL%
if %ERR% neq 0 pause
exit /b %ERR%
