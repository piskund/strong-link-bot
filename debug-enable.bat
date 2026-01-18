@echo off
REM ============================================================================
REM Strong Link Bot - Enable Debug Mode
REM Enables detailed debug logging to debug-logs directory
REM ============================================================================

echo.
echo ================================
echo   Enable Debug Mode
echo ================================
echo.

cd /d "%~dp0"

REM Check if .env exists
if not exist ".env" (
    echo [ERROR] .env file not found!
    echo Please run setup.bat first to create .env file.
    pause
    exit /b 1
)

echo This will enable DEBUG MODE which:
echo   - Logs ALL debug-level messages
echo   - Writes detailed logs to debug-logs\ directory
echo   - Increases log file size significantly
echo   - May impact performance slightly
echo.

set /p CONFIRM="Enable debug mode? (Y/N): "
if /i not "%CONFIRM%"=="Y" (
    echo Debug mode not enabled.
    pause
    exit /b 0
)

REM Check if DEBUG_MODE line already exists
findstr /C:"DEBUG_MODE=" .env >nul
if %errorlevel%==0 (
    echo.
    echo Updating existing DEBUG_MODE setting...
    REM Use PowerShell to replace the line
    powershell -Command "(Get-Content .env) -replace '^#?\s*DEBUG_MODE=.*', 'DEBUG_MODE=true' | Set-Content .env"
) else (
    echo.
    echo Adding DEBUG_MODE=true to .env...
    echo DEBUG_MODE=true >> .env
)

echo.
echo ================================
echo   Debug mode enabled!
echo ================================
echo.
echo Debug logs will be written to: debug-logs\
echo.
echo To apply changes, restart the bot:
echo   - Quick restart: restart.bat
echo   - Full restart: start-fresh.bat
echo.

set /p RESTART="Restart bot now? (Y/N): "
if /i "%RESTART%"=="Y" (
    echo.
    echo Restarting bot...
    docker-compose restart
    echo.
    echo Bot restarted with debug mode enabled!
    echo.
    echo To view debug logs:
    echo   - Live: docker-compose logs -f
    echo   - File: Open debug-logs\stronglink-debug-YYYY-MM-DD.log
    echo.
) else (
    echo.
    echo Remember to restart the bot to apply changes!
    echo.
)

pause
