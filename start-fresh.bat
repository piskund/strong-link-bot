@echo off
REM ============================================================================
REM Strong Link Bot - Fresh Start Script
REM Stops existing container, rebuilds, and starts with fresh image
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Fresh Start
echo ================================
echo.

REM Navigate to the bot directory
cd /d "%~dp0"

REM Check if .env file exists
if not exist ".env" (
    echo [ERROR] .env file not found!
    echo.
    echo Please create .env file first:
    echo   1. Copy .env.docker to .env
    echo   2. Edit .env and add your BOT__TOKEN and OPENAI__APIKEY
    echo.
    pause
    exit /b 1
)

echo [1/5] Stopping existing container...
docker-compose down
echo.

echo [2/5] Pulling latest code from git...
git pull
echo.

echo [3/5] Building fresh Docker image...
docker-compose build --no-cache
echo.

echo [4/5] Starting container...
docker-compose up -d
echo.

echo [5/5] Waiting for container to start...
timeout /t 3 /nobreak >nul
echo.

echo ================================
echo   Bot started successfully!
echo ================================
echo.
echo Container status:
docker-compose ps
echo.
echo To view logs, run: docker-compose logs -f
echo To stop the bot, run: docker-compose down
echo.
echo Opening logs in 3 seconds...
timeout /t 3 /nobreak >nul

REM Show logs
docker-compose logs -f

pause
