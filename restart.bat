@echo off
REM ============================================================================
REM Strong Link Bot - Restart Script
REM Restarts the container without rebuilding
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Restart
echo ================================
echo.

cd /d "%~dp0"

echo Restarting container...
docker-compose restart
echo.

echo Waiting for container to restart...
timeout /t 3 /nobreak >nul
echo.

echo ================================
echo   Bot restarted successfully!
echo ================================
echo.
docker-compose ps
echo.

pause
