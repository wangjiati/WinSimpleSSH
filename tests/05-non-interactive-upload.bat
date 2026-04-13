@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 05: Non-interactive upload
echo ============================================================
echo.

:: Create a test file
set TESTFILE=%TEMP%\sshc_test_upload_%RANDOM%.txt
echo WinSimpleSSH upload test - %DATE% %TIME% > "%TESTFILE%"
echo Test content line 2 >> "%TESTFILE%"

echo Uploading: %TESTFILE%
"%SSHC%" upload %HOST% -u %USER% -p %PASS% "%TESTFILE%" "C:\sshc_test_upload.txt"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] Upload succeeded
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)

del "%TESTFILE%" 2>nul
pause
