@echo off
set SSHC=%~dp0..\src\SSHClient\bin\Debug\net452\SSHC.exe
echo ============================================================
echo  TEST 14: Error - missing arguments
echo ============================================================
echo.

echo --- 14a: No args (should show usage) ---
"%SSHC%"
echo Exit: %ERRORLEVEL%
echo.

echo --- 14b: Missing host ---
"%SSHC%" connect
echo Exit: %ERRORLEVEL%
echo.

echo --- 14c: Missing username ---
"%SSHC%" connect 192.168.1.1
echo Exit: %ERRORLEVEL%
echo.

echo --- 14d: Non-interactive missing password ---
"%SSHC%" exec 192.168.1.1 -u admin "dir"
echo Exit: %ERRORLEVEL%
echo.

echo --- 14e: Unknown verb ---
"%SSHC%" blah
echo Exit: %ERRORLEVEL%
echo.
pause
