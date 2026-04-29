@echo off
REM ============================================================================
REM Strong Link Bot - First Time Setup
REM Guides you through initial configuration
REM ============================================================================

echo.
echo ================================
echo   Strong Link Bot - Setup
echo ================================
echo.

cd /d "%~dp0"

echo This script will help you set up the bot for the first time.
echo.

REM Check if .env already exists
if exist ".env" (
    echo [!] .env file already exists.
    set /p OVERWRITE="Do you want to overwrite it? (Y/N): "
    if /i not "%OVERWRITE%"=="Y" (
        echo Setup cancelled. Existing .env file preserved.
        pause
        exit /b 0
    )
)

REM Check for .env existence
echo [1/4] Checking .env file...
if not exist ".env" (
    echo [ERROR] .env file not found!
    echo Please create .env file with your configuration.
    echo See documentation for required settings.
    pause
    exit /b 1
)
echo [OK] .env file found
echo.

echo [2/4] Configuration
echo --------------------------------
echo.
echo Please edit the .env file and set these values:
echo.
echo   1. BOT__TOKEN=your_telegram_bot_token_here
echo      Get token from @BotFather: https://t.me/botfather
echo.
echo   2. OPENAI__APIKEY=your_openai_api_key_here
echo      Get API key from: https://platform.openai.com/api-keys
echo.
echo   3. GAME__TOPICS=Шахматы,Космос,История,...
echo      (Optional) Customize your quiz topics
echo.

set /p EDIT="Open .env file in Notepad now? (Y/N): "
if /i "%EDIT%"=="Y" (
    notepad .env
)
echo.

echo [3/4] Checking Docker...
docker --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker is not installed or not running!
    echo.
    echo Please install Docker Desktop from:
    echo https://www.docker.com/products/docker-desktop
    echo.
    pause
    exit /b 1
) else (
    echo [OK] Docker is installed
)
echo.

echo [4/4] Ready to build!
echo.

set /p BUILD="Build and start the bot now? (Y/N): "
if /i "%BUILD%"=="Y" (
    echo.
    echo Building Docker image...
    docker-compose build
    echo.
    echo Starting container...
    docker-compose up -d
    echo.
    echo ================================
    echo   Setup completed successfully!
    echo ================================
    echo.
    echo The bot is now running!
    echo.
    echo Next steps:
    echo   - View logs: double-click logs.bat
    echo   - Stop bot: double-click stop.bat
    echo   - Restart: double-click restart.bat
    echo.
    echo To view logs now, press any key...
    pause >nul
    docker-compose logs -f
) else (
    echo.
    echo Setup completed. To start the bot, run:
    echo   start.bat (quick start)
    echo   or
    echo   start-fresh.bat (full rebuild)
    echo.
)

pause
