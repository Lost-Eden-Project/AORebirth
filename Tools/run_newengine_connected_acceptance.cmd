@echo off
setlocal
if not "%~1"=="--engine" goto :usage
if "%~2"=="" goto :usage
if not "%~3"=="--login-engine" goto :usage
if "%~4"=="" goto :usage
if "%~5"=="--chests-only" goto :chests
if "%~5"=="--lifecycle-only" goto :lifecycle
if not "%~5"=="" goto :usage
dotnet run --project "%~dp0ZoneEngineSchemaValidation\ZoneEngineSchemaValidation.csproj" --configuration Release -- --run-connected --engine "%~2" --login-engine "%~4"
exit /b %errorlevel%
:chests
set "connectedMode=--run-connected-chests"
goto :ownedWorld
:lifecycle
set "connectedMode=--run-connected-lifecycle"
:ownedWorld
if not "%~6"=="--world-engine" goto :usage
if "%~7"=="" goto :usage
if not "%~8"=="" goto :usage
dotnet run --project "%~dp0ZoneEngineSchemaValidation\ZoneEngineSchemaValidation.csproj" --configuration Release -p:MissionChestConnectedTests=true -- %connectedMode% --engine "%~2" --login-engine "%~4" --world-engine "%~7"
exit /b %errorlevel%
:usage
echo Usage: run_newengine_connected_acceptance.cmd --engine ABSOLUTE_ZONEENGINE_NEW_DLL --login-engine ABSOLUTE_LOGINENGINE_EXE_OR_DLL [--chests-only/--lifecycle-only --world-engine ABSOLUTE_WORLDENGINE_DLL]
exit /b 64
