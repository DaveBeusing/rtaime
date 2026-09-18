:: Copyright (c) Dave Beusing <david.beusing@gmail.com>.
@echo off
setlocal
where pwsh.exe >nul 2>&1
if errorlevel 1 (
	echo rtaime requires PowerShell 7 (pwsh.exe) for the managed showcase launcher.
	pause
	exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Invoke-InvestorDemo.ps1" -InstallPath "%~dp0.."
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
	echo.
	echo rtaime showcase startup failed. See the message above for details.
	pause
)
exit /b %EXITCODE%
