@echo off
REM ============================================================================
REM Strong Link Bot - Debug Mode
REM Usage: debug.bat enable   — turn on debug logging
REM        debug.bat disable  — turn off debug logging
REM        debug.bat view     — view recent debug log files
REM ============================================================================

cd /d "%~dp0\.."

if /i "%1"=="enable"  goto :enable
if /i "%1"=="disable" goto :disable
if /i "%1"=="view"    goto :view

echo Usage:
echo   scripts\debug.bat enable   -- turn on debug logging
echo   scripts\debug.bat disable  -- turn off debug logging
echo   scripts\debug.bat view     -- view recent debug log files
echo.
pause
exit /b 0

REM ── enable ──────────────────────────────────────────────────────────────────
:enable
echo.
echo ================================
echo   Enable Debug Mode
echo ================================
echo.

if not exist ".env" (
    echo [ERROR] .env not found. Run scripts\setup.bat first.
    pause
    exit /b 1
)

findstr /C:"DEBUG_MODE=" .env >nul
if %errorlevel%==0 (
    powershell -Command "(Get-Content .env) -replace '^#?\s*DEBUG_MODE=.*', 'DEBUG_MODE=true' | Set-Content .env"
) else (
    echo DEBUG_MODE=true >> .env
)

echo Debug mode enabled. Logs will be written to: debug-logs\
echo.
set /p RESTART="Restart bot now to apply? (Y/N): "
if /i "%RESTART%"=="Y" docker-compose restart
echo.
pause
exit /b 0

REM ── disable ─────────────────────────────────────────────────────────────────
:disable
echo.
echo ================================
echo   Disable Debug Mode
echo ================================
echo.

if not exist ".env" (
    echo .env not found — debug mode is already off by default.
    pause
    exit /b 0
)

findstr /C:"DEBUG_MODE=" .env >nul
if %errorlevel%==0 (
    powershell -Command "(Get-Content .env) -replace '^DEBUG_MODE=true', '#DEBUG_MODE=false' | Set-Content .env"
    echo Debug mode disabled.
) else (
    echo DEBUG_MODE not set in .env — already disabled.
)

echo.
set /p RESTART="Restart bot now to apply? (Y/N): "
if /i "%RESTART%"=="Y" docker-compose restart
echo.
pause
exit /b 0

REM ── view ────────────────────────────────────────────────────────────────────
:view
echo.
echo ================================
echo   View Debug Logs
echo ================================
echo.

if not exist "debug-logs" (
    echo debug-logs\ directory does not exist yet.
    echo Run: scripts\debug.bat enable  then restart the bot.
    pause
    exit /b 0
)

set COUNT=0
for %%F in (debug-logs\*.log) do set /a COUNT+=1

if %COUNT%==0 (
    echo No log files in debug-logs\ — is debug mode enabled?
    echo Run: scripts\debug.bat enable
    pause
    exit /b 0
)

echo Found %COUNT% log file(s).
echo.

for /f "delims=" %%F in ('dir /b /o-d debug-logs\*.log 2^>nul') do (
    set "RECENT=%%F"
    goto :view_menu
)

:view_menu
echo Most recent: %RECENT%
echo.
echo [1] Open debug-logs\ in Explorer
echo [2] Print last 50 lines to console
echo [3] Open in Notepad
echo.
set /p CHOICE="Choice (1-3): "

if "%CHOICE%"=="1" explorer "debug-logs"
if "%CHOICE%"=="2" powershell -Command "Get-Content 'debug-logs\%RECENT%' -Tail 50"
if "%CHOICE%"=="3" notepad "debug-logs\%RECENT%"
echo.
pause
exit /b 0
