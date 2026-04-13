@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 06: Non-interactive download
echo ============================================================
echo.

set LOCALFILE=%TEMP%\sshc_test_download_%RANDOM%.txt

echo Downloading to: %LOCALFILE%
"%SSHC%" download %HOST% -u %USER% -p %PASS% "C:\sshc_test_upload.txt" "%LOCALFILE%"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] Download succeeded
    echo --- Downloaded content ---
    type "%LOCALFILE%"
    echo --- End ---
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
    echo Note: Run test 05 first to create the remote file
)

del "%LOCALFILE%" 2>nul
pause
