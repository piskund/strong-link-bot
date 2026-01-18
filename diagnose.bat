@echo off
REM ============================================================================
REM Strong Link Bot - Diagnostic Script
REM Checks logs and status to diagnose startup issues
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Diagnostics
echo ================================
echo.

cd /d "%~dp0"

echo [1/5] Checking if Docker is running...
docker --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker is not running!
    echo Please start Docker Desktop and try again.
    pause
    exit /b 1
) else (
    echo [OK] Docker is running
)
echo.

echo [2/5] Checking .env file...
if not exist ".env" (
    echo [ERROR] .env file not found!
    echo Please run setup.bat first.
    pause
    exit /b 1
) else (
    echo [OK] .env file exists

    REM Check for required variables
    findstr /C:"BOT__TOKEN=" .env >nul
    if errorlevel 1 (
        echo [WARNING] BOT__TOKEN not found in .env
    ) else (
        echo [OK] BOT__TOKEN found in .env
    )

    findstr /C:"OPENAI__APIKEY=" .env >nul
    if errorlevel 1 (
        echo [WARNING] OPENAI__APIKEY not found in .env
    ) else (
        echo [OK] OPENAI__APIKEY found in .env
    )
)
echo.

echo [3/5] Checking container status...
docker-compose ps
echo.

echo [4/5] Getting last 50 lines of container logs...
echo ================================
docker-compose logs --tail=50
echo ================================
echo.

echo [5/5] Checking for common issues...
echo.

REM Check if container is running
docker-compose ps | findstr "Up" >nul
if errorlevel 1 (
    echo [!] Container is NOT running
    echo.
    echo Possible causes:
    echo   1. Invalid BOT__TOKEN or OPENAI__APIKEY in .env
    echo   2. Configuration error in appsettings.json
    echo   3. Missing dependencies or build error
    echo   4. Debug mode configuration issue
    echo.
    echo Checking for specific errors in logs...
    echo.

    docker-compose logs 2>&1 | findstr /I "error exception failed" >nul
    if not errorlevel 1 (
        echo [!] Found errors in logs (see above)
    )

    docker-compose logs 2>&1 | findstr /I "DEBUG_MODE" >nul
    if not errorlevel 1 (
        echo [!] Debug mode messages found
    )
) else (
    echo [OK] Container is running!
)
echo.

echo ================================
echo   Diagnostic Complete
echo ================================
echo.
echo If the container is not running, review the logs above.
echo Common fixes:
echo   1. Check BOT__TOKEN and OPENAI__APIKEY in .env
echo   2. Try: docker-compose down ^&^& docker-compose build --no-cache ^&^& docker-compose up -d
echo   3. Disable debug mode if enabled: comment out DEBUG_MODE=true in .env
echo.

pause
