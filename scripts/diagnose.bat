@echo off
REM ============================================================================
REM Strong Link Bot - Diagnose and Fix
REM Checks container health, shows logs, and optionally does a clean rebuild.
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Diagnose
echo ================================
echo.

cd /d "%~dp0\.."

echo [1/3] Docker...
docker --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker is not running. Start Docker Desktop and try again.
    pause
    exit /b 1
)
echo [OK] Docker is running
echo.

echo [2/3] .env file...
if not exist ".env" (
    echo [ERROR] .env not found. Create it from env_template.txt.
    pause
    exit /b 1
)
findstr /C:"BOT__TOKEN=" .env >nul
if errorlevel 1 (echo [WARN] BOT__TOKEN missing in .env) else (echo [OK] BOT__TOKEN present)
findstr /C:"OPENAI__APIKEY=" .env >nul
if errorlevel 1 (echo [WARN] OPENAI__APIKEY missing in .env) else (echo [OK] OPENAI__APIKEY present)
echo.

echo [3/3] Container status...
docker-compose ps
echo.
echo Last 50 log lines:
echo ----------------------------------------
docker-compose logs --tail=50
echo ----------------------------------------
echo.

docker-compose ps | findstr "Up" >nul
if errorlevel 1 (
    echo [!] Container is NOT running.
    echo.
    set /p FIX="Run a clean rebuild to fix it? (Y/N): "
    if /i "!FIX!"=="Y" (
        echo.
        echo Stopping...
        docker-compose down
        echo Pruning build cache...
        docker system prune -f
        echo Removing old image...
        docker-compose down --rmi local 2>nul
        echo Building (no cache)...
        docker-compose build --no-cache --progress=plain
        if errorlevel 1 (
            echo [ERROR] Build failed. Check errors above.
            pause
            exit /b 1
        )
        echo Starting...
        docker-compose up -d
        timeout /t 5 /nobreak >nul
        echo.
        docker-compose ps
        echo.
        docker-compose logs --tail=20
    )
) else (
    echo [OK] Container is running.
)
echo.

pause
