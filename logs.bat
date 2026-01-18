@echo off
REM ============================================================================
REM Strong Link Bot - View Logs
REM Shows real-time logs from the running container
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Live Logs
echo ================================
echo.
echo Press Ctrl+C to stop viewing logs
echo.

cd /d "%~dp0"

REM Follow logs in real-time
docker-compose logs -f
