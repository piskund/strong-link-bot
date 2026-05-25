@echo off
REM ============================================================================
REM Strong Link Bot - Stop Script
REM Stops the running container
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Stop
echo ================================
echo.

cd /d "%~dp0\.."

echo Stopping container...
docker-compose down
echo.

echo ================================
echo   Bot stopped successfully!
echo ================================
echo.

pause
