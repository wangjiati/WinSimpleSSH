@echo off
:: ============================================================
::  TEST 16: SSHServer help command / -h / --help / -? / /?
::
::  SSHServer.exe is a GUI-subsystem (WinExe) binary. The help
::  path must print usage and exit immediately WITHOUT starting
::  the WebSocket server.
::
::  This test needs NO server. For each variant check:
::    - exit code 0
::    - output contains the "Usage" banner
::    - no new SSHServer.exe process is left running
::
::  NOTE: variants are passed quoted ("/?") - a bare /? would be
::  swallowed by CALL as its own help switch. Process counting
::  uses findstr plus an explicit System32\find.exe path so the
::  script also works when Git Bash shadows find.exe on PATH.
:: ============================================================
call "%~dp0config.bat"
if %ERRORLEVEL% == 1 exit /b 1

if not exist "%SSHSRV%" (
    echo [ERROR] SSHServer.exe not found: %SSHSRV%
    echo Please run: dotnet build WinSimpleSSH.sln
    exit /b 1
)

echo ============================================================
echo  TEST 16: SSHServer help variants (print usage and exit)
echo ============================================================
echo.

set FAILED=0
set TMP_OUT=%TEMP%\sshsrv_help_out.txt
call :count_servers BEFORE_COUNT

call :check_variant "help"
call :check_variant "-h"
call :check_variant "--help"
call :check_variant "-?"
call :check_variant "/?"
call :check_variant "-help"
call :check_variant "/help"
call :check_variant "--usage"
call :check_variant "HELP"
del "%TMP_OUT%" >nul 2>&1

:: Help must not leave a new server process running. Preserve any instance
:: that was already running before the test instead of terminating it.
call :count_servers AFTER_COUNT
if not "%AFTER_COUNT%" == "%BEFORE_COUNT%" (
    echo [FAIL] SSHServer.exe process count changed: %BEFORE_COUNT% ^> %AFTER_COUNT%
    set FAILED=1
) else (
    echo [PASS] no new SSHServer.exe process left running
)

echo.
if %FAILED% == 0 (
    echo TEST 16: ALL PASSED
) else (
    echo TEST 16: FAILED
)
pause
exit /b %FAILED%

:check_variant
"%SSHSRV%" %~1 > "%TMP_OUT%" 2>&1
set RC=%ERRORLEVEL%
findstr /C:"Usage" "%TMP_OUT%" >nul 2>&1
set FOUND=%ERRORLEVEL%
if not "%RC%" == "0" (
    echo [FAIL] %~1 - exit code %RC%
    set FAILED=1
    goto :eof
)
if not "%FOUND%" == "0" (
    echo [FAIL] %~1 - no Usage banner in output
    set FAILED=1
    goto :eof
)
echo [PASS] %~1
goto :eof

:count_servers
for /f %%C in ('tasklist /FI "IMAGENAME eq SSHServer.exe" /NH 2^>nul ^| findstr /I /C:"SSHServer.exe" ^| %SystemRoot%\System32\find.exe /C /V ""') do set "%~1=%%C"
goto :eof
