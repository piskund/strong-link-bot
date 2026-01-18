@echo off
REM ============================================================================
REM Strong Link Bot - Disable Debug Mode
REM Disables debug logging and reverts to normal logging
REM ============================================================================

echo.
echo ================================
echo   Disable Debug Mode
echo ================================
echo.

cd /d "%~dp0"

REM Check if .env exists
if not exist ".env" (
    echo [ERROR] .env file not found!
    echo Debug mode is already disabled (default).
    pause
    exit /b 0
)

echo This will disable DEBUG MODE and revert to normal logging.
echo Debug log files in debug-logs\ will be preserved.
echo.

set /p CONFIRM="Disable debug mode? (Y/N): "
if /i not "%CONFIRM%"=="Y" (
    echo Debug mode not changed.
    pause
    exit /b 0
)

REM Check if DEBUG_MODE line exists
findstr /C:"DEBUG_MODE=" .env >nul
if %errorlevel%==0 (
    echo.
    echo Disabling DEBUG_MODE...
    REM Use PowerShell to comment out or set to false
    powershell -Command "(Get-Content .env) -replace '^DEBUG_MODE=true', '#DEBUG_MODE=false' | Set-Content .env"
) else (
    echo.
    echo DEBUG_MODE not found in .env (already disabled).
)

echo.
echo ================================
echo   Debug mode disabled!
echo ================================
echo.
echo Normal logging will be used after restart.
echo Previous debug logs are preserved in: debug-logs\
echo.

set /p RESTART="Restart bot now? (Y/N): "
if /i "%RESTART%"=="Y" (
    echo.
    echo Restarting bot...
    docker-compose restart
    echo.
    echo Bot restarted with normal logging.
    echo.
) else (
    echo.
    echo Remember to restart the bot to apply changes!
    echo.
)

pause
