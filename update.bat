@echo off
REM ============================================================================
REM Strong Link Bot - Update Script
REM Pulls latest code from git, rebuilds, and restarts
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Update
echo ================================
echo.

cd /d "%~dp0"

echo [1/6] Checking for git updates...
git fetch
echo.

echo Current branch status:
git status
echo.

set /p CONFIRM="Do you want to pull latest changes and rebuild? (Y/N): "
if /i not "%CONFIRM%"=="Y" (
    echo Update cancelled.
    pause
    exit /b 0
)

echo.
echo [2/6] Pulling latest code...
git pull
echo.

echo [3/6] Stopping existing container...
docker-compose down
echo.

echo [4/6] Building new Docker image...
docker-compose build --no-cache
echo.

echo [5/6] Starting updated container...
docker-compose up -d
echo.

echo [6/6] Waiting for container to start...
timeout /t 3 /nobreak >nul
echo.

echo ================================
echo   Update completed successfully!
echo ================================
echo.
docker-compose ps
echo.

pause
