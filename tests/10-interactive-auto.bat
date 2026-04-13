@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 10: Interactive mode - -p password (auto login)
echo ============================================================
echo.
echo Will connect with password, skipping prompt. After login, type:
echo   ipconfig /all
echo   exit
echo.
pause
"%SSHC%" connect %HOST% -u %USER% -p %PASS%
echo.
echo Exit code: %ERRORLEVEL%
pause
