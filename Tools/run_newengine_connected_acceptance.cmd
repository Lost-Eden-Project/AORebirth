@echo off
setlocal
if not "%~1"=="--engine" goto :usage
if "%~2"=="" goto :usage
if not "%~3"=="--login-engine" goto :usage
if "%~4"=="" goto :usage
if "%~5"=="--chests-only" goto :chests
if not "%~5"=="" goto :usage
dotnet run --project "%~dp0ZoneEngineSchemaValidation\ZoneEngineSchemaValidation.csproj" --configuration Release -- --run-connected --engine "%~2" --login-engine "%~4"
exit /b %errorlevel%
:chests
if not "%~6"=="" goto :usage
dotnet run --project "%~dp0ZoneEngineSchemaValidation\ZoneEngineSchemaValidation.csproj" --configuration Release -p:MissionChestConnectedTests=true -- --run-connected-chests --engine "%~2" --login-engine "%~4"
exit /b %errorlevel%
:usage
echo Usage: run_newengine_connected_acceptance.cmd --engine ABSOLUTE_ZONEENGINE_NEW_DLL --login-engine ABSOLUTE_LOGINENGINE_EXE_OR_DLL [--chests-only]
exit /b 64
