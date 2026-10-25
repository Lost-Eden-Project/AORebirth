@echo off
setlocal EnableExtensions
call "%~dp0select_python_runtime.cmd"
if errorlevel 1 exit /b 1
%AO_REBIRTH_PYTHON% "%~dp0windows_release_contract.py" %*
exit /b %errorlevel%
