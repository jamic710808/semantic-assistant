@echo off
setlocal
set "TASK_LAUNCHER=%~dp0Start-Semantic-Assistant.cmd"
if not exist "%TASK_LAUNCHER%" (
  echo Semantic Assistant launcher is missing. Expected:
  echo "%TASK_LAUNCHER%"
  pause
  exit /b 1
)
call "%TASK_LAUNCHER%"
exit /b %errorlevel%
