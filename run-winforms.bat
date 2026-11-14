@echo off
rem Builds, then starts the WinForms front end (same library, same config and autosave folder).
setlocal
cd /d "%~dp0"
dotnet build src\DesktopSqlStudio.WinForms\DesktopSqlStudio.WinForms.csproj -c Release -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
start "" "src\DesktopSqlStudio.WinForms\bin\Release\net10.0-windows\DesktopSqlStudio.WinForms.exe"
endlocal
