@echo off
REM ============================================================================
REM Strong Link Bot - Quick Start Script
REM Starts the bot using existing image (fast start, no rebuild)
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Quick Start
echo ================================
echo.

REM Navigate to the bot directory
cd /d "%~dp0"

REM Check if .env file exists
if not exist ".env" (
    echo [ERROR] .env file not found!
    echo.
    echo Please create .env file first with your configuration.
    echo Required settings: BOT__TOKEN and OPENAI__APIKEY
    echo.
    pause
    exit /b 1
)

echo Creating data directories if needed...
if not exist "data\pool" mkdir "data\pool"
if not exist "data\state" mkdir "data\state"
if not exist "data\results" mkdir "data\results"
if not exist "logs" mkdir "logs"
if not exist "debug-logs" mkdir "debug-logs"
echo.

echo Starting container...
docker-compose up -d
echo.

echo Waiting for container to start...
timeout /t 2 /nobreak >nul
echo.

echo ================================
echo   Bot started successfully!
echo ================================
echo.
docker-compose ps
echo.
echo To view logs: docker-compose logs -f
echo To stop: docker-compose down
echo.

pause
