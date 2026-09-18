:: Copyright (c) Dave Beusing <david.beusing@gmail.com>.
@echo off
setlocal
where pwsh.exe >nul 2>&1
if errorlevel 1 (
	echo rtaime requires PowerShell 7 (pwsh.exe) for the managed showcase launcher.
	pause
	exit /b 1
)

set "SHOWCASE_SCRIPT=%~dp0Invoke-InvestorDemo.ps1"
set "INSTALL_ROOT=%~dp0.."
if not exist "%SHOWCASE_SCRIPT%" (
	set "SHOWCASE_SCRIPT=%~dp0tools\Invoke-InvestorDemo.ps1"
	set "INSTALL_ROOT=%~dp0"
)
if not exist "%SHOWCASE_SCRIPT%" (
	echo rtaime showcase launcher was not found in this installed release.
	pause
	exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SHOWCASE_SCRIPT%" -InstallPath "%INSTALL_ROOT%"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
	echo.
	echo rtaime showcase startup failed. See the message above for details.
	pause
)
exit /b %EXITCODE%
