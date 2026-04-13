@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 03: Non-interactive exec - password via env var
echo ============================================================
echo.
set SSHC_PASSWORD=%PASS%
"%SSHC%" exec %HOST% -u %USER% "ipconfig /all"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] Env var password worked
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)
set SSHC_PASSWORD=
pause
