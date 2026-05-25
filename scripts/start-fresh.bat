@echo off
REM ============================================================================
REM Strong Link Bot - Fresh Start
REM Pulls latest code from git, does a clean --no-cache rebuild, and starts.
REM Use this after pulling changes or when the image feels stale.
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Fresh Start
echo ================================
echo.

cd /d "%~dp0\.."

if not exist ".env" (
    echo [ERROR] .env file not found!
    echo Please create .env from env_template.txt and fill in BOT__TOKEN and OPENAI__APIKEY.
    pause
    exit /b 1
)

echo [1/5] Stopping existing container...
docker-compose down
echo.

echo [2/5] Pulling latest code from git...
git pull
echo.

echo [3/5] Creating data directories...
if not exist "data\pool"    mkdir "data\pool"
if not exist "data\state"   mkdir "data\state"
if not exist "data\results" mkdir "data\results"
if not exist "logs"         mkdir "logs"
if not exist "debug-logs"   mkdir "debug-logs"
echo.

echo [4/5] Building fresh Docker image (no cache)...
docker-compose build --no-cache
if errorlevel 1 (
    echo [ERROR] Build failed. See errors above.
    pause
    exit /b 1
)
echo.

echo [5/5] Starting container...
docker-compose up -d
echo.

timeout /t 3 /nobreak >nul

echo ================================
echo   Bot started!
echo ================================
echo.
docker-compose ps
echo.

echo Opening logs (Ctrl+C to stop)...
timeout /t 2 /nobreak >nul
docker-compose logs -f

pause
