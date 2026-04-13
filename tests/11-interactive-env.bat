@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 11: Interactive mode - env var password (auto login)
echo ============================================================
echo.
echo Will connect using SSHC_PASSWORD env var. After login, type:
echo   ipconfig /all
echo   exit
echo.
pause
set SSHC_PASSWORD=%PASS%
"%SSHC%" connect %HOST% -u %USER%
set SSHC_PASSWORD=
echo.
echo Exit code: %ERRORLEVEL%
pause
