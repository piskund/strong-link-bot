@echo off
REM ============================================================================
REM Strong Link Bot - Start
REM Usage: start.bat          — start with existing image (fast)
REM        start.bat rebuild  — rebuild image then start
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Start
echo ================================
echo.

cd /d "%~dp0\.."

if not exist ".env" (
    echo [ERROR] .env file not found!
    echo Please create .env from env_template.txt and fill in BOT__TOKEN and OPENAI__APIKEY.
    pause
    exit /b 1
)

if not exist "data\pool"    mkdir "data\pool"
if not exist "data\state"   mkdir "data\state"
if not exist "data\results" mkdir "data\results"
if not exist "logs"         mkdir "logs"
if not exist "debug-logs"   mkdir "debug-logs"

if /i "%1"=="rebuild" (
    echo Rebuilding image...
    docker-compose build
    if errorlevel 1 (
        echo [ERROR] Build failed. See errors above.
        pause
        exit /b 1
    )
    echo.
)

echo Starting container...
docker-compose up -d
echo.

timeout /t 2 /nobreak >nul

echo ================================
echo   Bot started!
echo ================================
echo.
docker-compose ps
echo.
echo Tip: run  scripts\start.bat rebuild  to rebuild before starting.
echo.

pause
