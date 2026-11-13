@echo off
rem Builds, then starts the app through the dotnet host (DesktopSqlStudio.dll),
rem which avoids launching the unsigned DesktopSqlStudio.exe.
setlocal
cd /d "%~dp0"
dotnet build src\DesktopSqlStudio\DesktopSqlStudio.csproj -c Release -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
start "" dotnet "src\DesktopSqlStudio\bin\Release\net10.0-windows\DesktopSqlStudio.dll"
endlocal
