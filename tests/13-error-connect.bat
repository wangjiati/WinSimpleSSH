@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 13: Error - unreachable host (expect exit 255)
echo ============================================================
echo.
"%SSHC%" exec 192.0.2.1 -u %USER% -p %PASS% "echo should not appear"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 255 (
    echo [PASS] Connection failed as expected (exit 255)
) else (
    echo [FAIL] Expected 255, got %ERRORLEVEL%
)
pause
