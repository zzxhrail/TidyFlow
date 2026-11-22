@echo off
rem 用 VS Code 打开本项目。
rem --disable-workspace-trust：跳过"是否信任此文件夹"的确认，
rem 否则 C# 扩展会以受限模式启动，没有智能提示与跳转。
setlocal
set CODE=code
where code >nul 2>nul
if errorlevel 1 set CODE="%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd"
start "" %CODE% --disable-workspace-trust "%~dp0"
endlocal
