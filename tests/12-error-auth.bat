@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 12: Error - wrong password (expect exit 254)
echo ============================================================
echo.
"%SSHC%" exec %HOST% -u %USER% -p wrongpassword "echo should not appear"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 254 (
    echo [PASS] Auth failed as expected (exit 254)
) else (
    echo [FAIL] Expected 254, got %ERRORLEVEL%
)
pause
