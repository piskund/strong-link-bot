@echo off
REM ============================================================================
REM Strong Link Bot - View Debug Logs
REM Opens the debug log directory and shows recent logs
REM ============================================================================

echo.
echo ================================
echo   View Debug Logs
echo ================================
echo.

cd /d "%~dp0"

REM Check if debug-logs directory exists
if not exist "debug-logs" (
    echo [!] debug-logs directory does not exist yet.
    echo.
    echo To enable debug logging:
    echo   1. Run debug-enable.bat
    echo   2. Restart the bot
    echo   3. Debug logs will appear in debug-logs\
    echo.
    pause
    exit /b 0
)

REM Count log files
set COUNT=0
for %%F in (debug-logs\*.log) do set /a COUNT+=1

if %COUNT%==0 (
    echo [!] No debug log files found in debug-logs\
    echo.
    echo Debug mode might not be enabled.
    echo Run debug-enable.bat to enable it.
    echo.
    pause
    exit /b 0
)

echo Found %COUNT% debug log file(s) in debug-logs\
echo.

REM Find the most recent log file
for /f "delims=" %%F in ('dir /b /o-d debug-logs\*.log 2^>nul') do (
    set "RECENT=%%F"
    goto :found
)

:found
echo Most recent log file: %RECENT%
echo.

echo What would you like to do?
echo.
echo [1] Open debug-logs folder in Explorer
echo [2] View last 50 lines of recent log in console
echo [3] Open recent log in Notepad
echo [4] Copy debug-logs folder path to clipboard
echo [5] Exit
echo.

set /p CHOICE="Enter choice (1-5): "

if "%CHOICE%"=="1" (
    echo.
    echo Opening debug-logs folder...
    explorer "debug-logs"
    goto :end
)

if "%CHOICE%"=="2" (
    echo.
    echo ================================
    echo   Last 50 lines of %RECENT%
    echo ================================
    echo.
    powershell -Command "Get-Content 'debug-logs\%RECENT%' -Tail 50"
    echo.
    goto :end
)

if "%CHOICE%"=="3" (
    echo.
    echo Opening in Notepad...
    notepad "debug-logs\%RECENT%"
    goto :end
)

if "%CHOICE%"=="4" (
    echo.
    echo Copying path to clipboard...
    echo %CD%\debug-logs | clip
    echo Path copied: %CD%\debug-logs
    echo.
    goto :end
)

:end
pause
