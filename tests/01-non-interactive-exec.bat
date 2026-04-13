@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 01: Non-interactive exec - basic command (ipconfig /all)
echo ============================================================
echo.
"%SSHC%" exec %HOST% -u %USER% -p %PASS% "ipconfig /all"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] Command succeeded
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)
pause
