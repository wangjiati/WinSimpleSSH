@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 02: Non-interactive exec --json output
echo ============================================================
echo.
"%SSHC%" exec %HOST% -u %USER% -p %PASS% --json "ipconfig /all"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] JSON output succeeded
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)
pause
