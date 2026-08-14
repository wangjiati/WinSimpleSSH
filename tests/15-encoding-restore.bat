@echo off
:: ============================================================
::  TEST 15: Console codepage restore after non-interactive run
::
::  SSHC non-interactive mode sets Console.OutputEncoding=UTF8,
::  which is SetConsoleOutputCP(65001) on the console SHARED with
::  the parent cmd.exe, and the change outlives the process.
::  cmd.exe decodes each .bat line with the codepage current at
::  read time, so a GBK .bat garbles every line after the first
::  SSHC call unless the original codepage is restored on exit.
::
::  This test needs NO server: even a failed connection flips the
::  codepage. PASS = codepage identical before/after the call.
:: ============================================================
call "%~dp0config.bat"
if %ERRORLEVEL% == 1 exit /b 1

echo ============================================================
echo  TEST 15: Console codepage restore (expect same CP before/after)
echo ============================================================
echo.

for /f "tokens=2 delims=:" %%a in ('chcp') do set "CP_BEFORE=%%a"
set "CP_BEFORE=%CP_BEFORE: =%"

:: Connection to a closed local port fails fast (exit 255) - fine,
:: we only care about the console codepage after SSHC exits.
"%SSHC%" exec 127.0.0.1 --port 1 -u test -p test "echo x" >nul 2>&1

for /f "tokens=2 delims=:" %%a in ('chcp') do set "CP_AFTER=%%a"
set "CP_AFTER=%CP_AFTER: =%"

echo Codepage before: %CP_BEFORE%
echo Codepage after : %CP_AFTER%
echo.
if "%CP_BEFORE%" == "%CP_AFTER%" (
    echo [PASS] Console codepage restored
) else (
    echo [FAIL] Codepage changed %CP_BEFORE% to %CP_AFTER% - .bat lines will garble
)
pause
