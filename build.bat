@echo off
setlocal
cd /d "%~dp0"
echo Building Desktop SQL Studio...
dotnet build DesktopSqlStudio.sln -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
echo.
echo Build succeeded.
endlocal
