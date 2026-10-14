@echo off
setlocal
set "TASK_APP=%~dp0app\SemanticAssistant.Desktop.dll"
set "TASK_DOTNET=%~dp0..\.tools\dotnet10\dotnet.exe"
if not exist "%TASK_APP%" (
  echo Semantic Assistant desktop build is missing:
  echo "%TASK_APP%"
  pause
  exit /b 1
)
if exist "%TASK_DOTNET%" (
  "%TASK_DOTNET%" "%TASK_APP%"
) else (
  dotnet "%TASK_APP%"
)
set "TASK_RESULT=%errorlevel%"
if not "%TASK_RESULT%"=="0" pause
exit /b %TASK_RESULT%
