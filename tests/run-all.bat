@echo off
:: ============================================================
::  Run all non-interactive tests sequentially
::  Interactive tests (09-11) require manual operation
:: ============================================================
call "%~dp0config.bat"
if %ERRORLEVEL% == 1 exit /b 1

set PASS_COUNT=0
set FAIL_COUNT=0

echo ============================================================
echo  WinSimpleSSH Test Suite
echo  Host: %HOST%  User: %USER%  Port: %PORT%
echo ============================================================
echo.

:: --- Test 01: exec basic ---
echo [01/08] exec ipconfig /all ...
"%SSHC%" exec %HOST% -u %USER% -p %PASS% "ipconfig /all" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )

:: --- Test 02: exec --json ---
echo [02/08] exec --json ...
"%SSHC%" exec %HOST% -u %USER% -p %PASS% --json "hostname" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )

:: --- Test 03: exec env var password ---
echo [03/08] exec env var password ...
set SSHC_PASSWORD=%PASS%
"%SSHC%" exec %HOST% -u %USER% "hostname" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )
set SSHC_PASSWORD=

:: --- Test 04: start notepad ---
echo [04/08] start notepad ...
"%SSHC%" start %HOST% -u %USER% -p %PASS% "notepad.exe" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )

:: --- Test 05: upload ---
echo [05/08] upload ...
set UFILE=%TEMP%\sshc_test_%RANDOM%.txt
echo test > "%UFILE%"
"%SSHC%" upload %HOST% -u %USER% -p %PASS% "%UFILE%" "C:\sshc_test_upload.txt" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )
del "%UFILE%" 2>nul

:: --- Test 06: download ---
echo [06/08] download ...
set DFILE=%TEMP%\sshc_dl_%RANDOM%.txt
"%SSHC%" download %HOST% -u %USER% -p %PASS% "C:\sshc_test_upload.txt" "%DFILE%" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )
del "%DFILE%" 2>nul

:: --- Test 07: --port option ---
echo [07/08] exec --port ...
"%SSHC%" exec %HOST% --port %PORT% -u %USER% -p %PASS% "echo OK" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )

:: --- Test 08: -q quiet ---
echo [08/08] exec -q quiet ...
"%SSHC%" exec %HOST% -u %USER% -p %PASS% -q "hostname" >nul 2>&1
if %ERRORLEVEL% == 0 ( set /a PASS_COUNT+=1 && echo   [PASS] ) else ( set /a FAIL_COUNT+=1 && echo   [FAIL] exit=%ERRORLEVEL% )

echo.
echo ============================================================
echo  Results: %PASS_COUNT% passed, %FAIL_COUNT% failed
echo ============================================================
pause
