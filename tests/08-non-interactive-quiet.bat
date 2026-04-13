@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 08: Non-interactive exec - quiet mode (-q)
echo ============================================================
echo.
echo [Only command output should appear below, no banner]
echo ---BEGIN---
"%SSHC%" exec %HOST% -u %USER% -p %PASS% -q "hostname"
echo ---END---
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] Quiet mode worked
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)
pause
