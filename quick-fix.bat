@echo off
REM ============================================================================
REM Strong Link Bot - Quick Fix Script
REM Attempts to fix common startup issues
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Quick Fix
echo ================================
echo.

cd /d "%~dp0"

echo This script will:
echo   1. Stop any running containers
echo   2. Clean Docker build cache
echo   3. Rebuild from scratch
echo   4. Start the bot
echo   5. Show logs to diagnose issues
echo.

set /p CONFIRM="Continue? (Y/N): "
if /i not "%CONFIRM%"=="Y" (
    echo Cancelled.
    pause
    exit /b 0
)

echo.
echo [1/6] Stopping existing containers...
docker-compose down
echo.

echo [2/6] Cleaning Docker build cache...
docker system prune -f
echo.

echo [3/6] Removing old images...
docker-compose down --rmi local
echo.

echo [4/6] Building fresh image (this may take 2-3 minutes)...
docker-compose build --no-cache --progress=plain
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed!
    echo Check the error messages above.
    pause
    exit /b 1
)
echo.

echo [5/6] Starting container...
docker-compose up -d
echo.

echo [6/6] Waiting 5 seconds for startup...
timeout /t 5 /nobreak >nul
echo.

echo ================================
echo   Status Check
echo ================================
echo.
docker-compose ps
echo.

echo ================================
echo   Recent Logs
echo ================================
echo.
docker-compose logs --tail=30
echo.

echo ================================
echo   Action Items
echo ================================
echo.

docker-compose ps | findstr "Up" >nul
if errorlevel 1 (
    echo [!] Container is NOT running
    echo.
    echo Please review the logs above for errors.
    echo Common issues:
    echo   - Invalid BOT__TOKEN in .env
    echo   - Invalid OPENAI__APIKEY in .env
    echo   - Missing required configuration
    echo.
    echo To see full logs: docker-compose logs
    echo To edit .env: notepad .env
) else (
    echo [OK] Container is running!
    echo.
    echo The bot should be operational.
    echo To monitor logs: docker-compose logs -f
)
echo.

pause
