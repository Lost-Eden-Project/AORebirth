@echo off
setlocal
if not "%~2"=="" exit /b 2
set "validationMode=--run-disposable"
if "%~1"=="--expansion-entitlements" set "validationMode=--run-expansion-entitlements"
if not "%~1"=="" if not "%~1"=="--expansion-entitlements" exit /b 2
set "AO_REBIRTH_ALLOW_DISPOSABLE_ACCOUNT_DAO_VALIDATION=1"
dotnet run --project "%~dp0AccountDaoValidation\AccountDaoValidation.csproj" --configuration Release -- %validationMode%
exit /b %errorlevel%
