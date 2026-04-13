@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 04: Non-interactive start - launch notepad
echo ============================================================
echo.
"%SSHC%" start %HOST% -u %USER% -p %PASS% "notepad.exe"
echo.
echo Exit code: %ERRORLEVEL%
if %ERRORLEVEL% == 0 (
    echo [PASS] Start succeeded (check remote desktop for notepad)
) else (
    echo [FAIL] Exit code: %ERRORLEVEL%
)
pause
