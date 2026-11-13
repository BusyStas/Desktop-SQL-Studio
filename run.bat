@echo off
setlocal
cd /d "%~dp0"
dotnet build src\DesktopSqlStudio\DesktopSqlStudio.csproj -c Release -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
start "" dotnet run --project src\DesktopSqlStudio\DesktopSqlStudio.csproj -c Release --no-build
endlocal
