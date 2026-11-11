@echo off
setlocal
set "AMADEA_PY=%~dp0.runtime\python\cpython-3.12.12-windows-x86_64-none\python.exe"
if not exist "%AMADEA_PY%" (
  echo Run setup.cmd first.
  exit /b 1
)
set "PYTHONUTF8=1"
"%AMADEA_PY%" "%~dp0run.py"
exit /b %ERRORLEVEL%
