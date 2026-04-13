@echo off
:: ============================================================
::  测试配置 - 修改此处参数后运行其他测试脚本
:: ============================================================

set HOST=192.168.117.129
set USER=admin
set PASS=admin123
set PORT=22222

set SSHC=%~dp0..\src\SSHClient\bin\Debug\net452\SSHC.exe

if not exist "%SSHC%" (
    echo [ERROR] SSHC.exe not found: %SSHC%
    echo Please run: dotnet build WinSimpleSSH.sln
    exit /b 1
)
