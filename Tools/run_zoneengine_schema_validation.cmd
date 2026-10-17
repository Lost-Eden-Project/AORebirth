@echo off
setlocal
if "%~1"=="--run-character-creation" goto :creation
if not "%~1"=="--run-disposable" goto :usage
if not "%~2"=="--engine" goto :usage
if "%~3"=="" goto :usage
if not "%~4"=="" goto :usage
dotnet run --project "%~dp0ZoneEngineSchemaValidation\ZoneEngineSchemaValidation.csproj" --configuration Release -- --run-disposable --engine "%~3"
exit /b %errorlevel%
:creation
if not "%~2"=="" goto :usage
dotnet run --project "%~dp0ZoneEngineSchemaValidation\ZoneEngineSchemaValidation.csproj" --configuration Release -p:LoginCharacterCreationFocusedTests=true -- --run-character-creation
exit /b %errorlevel%
:usage
echo Usage: run_zoneengine_schema_validation.cmd --run-disposable --engine ABSOLUTE_ZONEENGINE_NEW_DLL
echo     or run_zoneengine_schema_validation.cmd --run-character-creation
exit /b 64
