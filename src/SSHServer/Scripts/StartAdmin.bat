@echo off
:: 获取当前目录路径并拼接 exe 名称，通过 PowerShell 以管理员身份运行
powershell -Command "Start-Process '%~dp0SSHServer.exe' -ArgumentList '--console' -Verb RunAs"