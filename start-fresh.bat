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
    echo Please create .env file first with your configuration.
    echo Required settings: BOT__TOKEN and OPENAI__APIKEY
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

echo [3/6] Creating data directories...
if not exist "data\pool" mkdir "data\pool"
if not exist "data\state" mkdir "data\state"
if not exist "data\results" mkdir "data\results"
if not exist "logs" mkdir "logs"
if not exist "debug-logs" mkdir "debug-logs"
echo Data directories created.
echo.

echo [4/6] Building fresh Docker image...
docker-compose build --no-cache
echo.

echo [5/6] Starting container...
docker-compose up -d
echo.

echo [6/6] Waiting for container to start...
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
