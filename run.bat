@echo off
rem Builds, then starts DesktopSqlStudio.exe directly.
rem If Windows blocks the exe (unsigned app), use run-dll.bat instead.
setlocal
cd /d "%~dp0"
dotnet build src\DesktopSqlStudio\DesktopSqlStudio.csproj -c Release -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
start "" "src\DesktopSqlStudio\bin\Release\net10.0-windows\DesktopSqlStudio.exe"
endlocal
pause