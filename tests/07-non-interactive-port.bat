@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 07: Non-interactive exec - --port option
echo ============================================================
echo.
"%SSHC%" exec %HOST% --port %PORT% -u %USER% -p %PASS% "echo port test OK"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] --port option worked
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)
pause
