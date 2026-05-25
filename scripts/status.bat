@echo off
REM ============================================================================
REM Strong Link Bot - Status Check
REM Shows current status of the bot container
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Status
echo ================================
echo.

cd /d "%~dp0\.."

echo Container Status:
docker-compose ps
echo.

echo Recent Logs (last 20 lines):
echo --------------------------------
docker-compose logs --tail=20
echo.

echo ================================
echo Data Directories:
echo ================================
if exist "data\pool" (
    echo [OK] data\pool exists
) else (
    echo [!] data\pool does not exist
)

if exist "data\state" (
    echo [OK] data\state exists
) else (
    echo [!] data\state does not exist
)

if exist "data\results" (
    echo [OK] data\results exists
) else (
    echo [!] data\results does not exist
)

if exist "logs" (
    echo [OK] logs directory exists
) else (
    echo [!] logs directory does not exist
)
echo.

pause
