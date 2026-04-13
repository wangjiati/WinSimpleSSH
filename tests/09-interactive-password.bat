@echo off
call "%~dp0config.bat"
echo ============================================================
echo  TEST 09: Interactive mode - password prompt
echo ============================================================
echo.
echo Will connect and prompt for password. After login, type:
echo   ipconfig /all
echo   exit
echo.
pause
"%SSHC%" connect %HOST% -u %USER%
echo.
echo Exit code: %ERRORLEVEL%
pause
