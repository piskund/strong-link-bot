# Desktop Scripts Guide for Strong Link Bot

This guide explains all the convenient batch scripts you can use to manage the bot from your Windows desktop.

## 📋 Quick Reference

| Script | Purpose | When to Use |
|--------|---------|-------------|
| `setup.bat` | First-time setup | Initial configuration only |
| `start-fresh.bat` ⭐ | Fresh start with rebuild | After code changes, use this for desktop |
| `start.bat` | Quick start | When nothing changed, fast startup |
| `stop.bat` | Stop the bot | To stop the running bot |
| `restart.bat` | Restart | Apply .env changes without rebuild |
| `logs.bat` | View live logs | Monitor bot activity |
| `status.bat` | Check status | See if bot is running |
| `update.bat` | Pull & rebuild | Get latest version from git |
| `debug-enable.bat` | Enable debug mode | Detailed logging for troubleshooting |
| `debug-disable.bat` | Disable debug mode | Return to normal logging |
| `debug-logs-view.bat` | View debug logs | Open/analyze debug log files |

## 🚀 First Time Setup

### Step 1: Run Setup Script

Double-click `setup.bat` - it will guide you through:

1. Creating `.env` file from template
2. Opening `.env` in Notepad for you to fill in:
   - `BOT__TOKEN` from @BotFather
   - `OPENAI__APIKEY` from OpenAI
   - `GAME__TOPICS` (optional customization)
3. Checking if Docker is installed
4. Building and starting the bot

### Step 2: Pin to Desktop

Right-click `start-fresh.bat` → Send to → Desktop (create shortcut)

Now you have a desktop icon to start the bot with one click!

## 📝 Detailed Script Descriptions

### setup.bat - First Time Setup

**What it does:**
- Checks if `.env` file exists
- Opens `.env` in Notepad for editing
- Checks if Docker is installed
- Offers to build and start the bot

**When to use:**
- First time setting up the bot
- After deleting `.env` file
- Setting up on a new machine

**Example:**
```
Double-click setup.bat
→ Edit .env with your tokens
→ Bot builds and starts automatically
```

---

### start-fresh.bat ⭐ - Fresh Start (Recommended for Desktop)

**What it does:**
1. Stops existing container
2. Pulls latest code from git
3. Builds fresh Docker image (no cache)
4. Starts new container
5. Shows live logs

**When to use:**
- **Daily use (recommended)** - Always get latest version
- After making code changes
- After git pull
- When you want to ensure clean state
- **Pin this to desktop!**

**Example:**
```
Double-click start-fresh.bat
→ Stops old container
→ Pulls latest code
→ Rebuilds everything
→ Starts fresh
→ Shows logs
```

**Time:** ~2-3 minutes (includes full rebuild)

---

### start.bat - Quick Start

**What it does:**
- Starts container using existing Docker image
- No rebuild, very fast

**When to use:**
- When you know nothing changed
- Quick restart after stop
- Testing if container works

**Example:**
```
Double-click start.bat
→ Starts immediately (5-10 seconds)
```

**Time:** ~5-10 seconds

---

### stop.bat - Stop the Bot

**What it does:**
- Stops the running container
- Preserves data (pool, state, results, logs)

**When to use:**
- Stop bot temporarily
- Before system shutdown
- Before manual maintenance

**Example:**
```
Double-click stop.bat
→ Container stops gracefully
→ Data preserved
```

---

### restart.bat - Restart Container

**What it does:**
- Restarts the container without rebuilding
- Reloads `.env` file
- Keeps same Docker image

**When to use:**
- After changing `.env` configuration
- After bot becomes unresponsive
- To apply new environment variables

**Example:**
```
Double-click restart.bat
→ Container restarts quickly
→ New .env values loaded
```

**Time:** ~5-10 seconds

---

### logs.bat - View Live Logs

**What it does:**
- Shows real-time logs from running container
- Follows new logs as they appear
- Press Ctrl+C to stop viewing

**When to use:**
- Monitor bot activity
- Debug issues
- See what questions are being asked
- Check for errors

**Example:**
```
Double-click logs.bat
→ Shows live logs
→ Ctrl+C to exit
```

**Tips:**
- Look for "[ERROR]" to find problems
- Check "[INFO]" for normal operations
- Watch for "Player X answered Y" during gameplay

---

### status.bat - Check Status

**What it does:**
- Shows if container is running
- Displays last 20 log lines
- Checks if data directories exist

**When to use:**
- Verify bot is running
- Quick health check
- See recent activity

**Example:**
```
Double-click status.bat
→ Shows container status
→ Shows recent logs
→ Checks data directories
```

---

### update.bat - Update to Latest Version

**What it does:**
1. Fetches latest changes from git
2. Shows what changed
3. Asks for confirmation
4. Pulls latest code
5. Stops container
6. Rebuilds with new code
7. Starts updated container

**When to use:**
- Get latest features/fixes
- After seeing update notification on GitHub
- Periodically (e.g., once a week)

**Example:**
```
Double-click update.bat
→ Shows git status
→ "Pull changes? (Y/N)"
→ Rebuilds with latest code
→ Starts updated bot
```

**Time:** ~3-5 minutes

---

### debug-enable.bat - Enable Debug Mode

**What it does:**
- Enables detailed debug logging
- Adds `DEBUG_MODE=true` to `.env` file
- Configures bot to log ALL debug messages
- Writes logs to `debug-logs/` directory
- Offers to restart bot to apply changes

**When to use:**
- Troubleshooting bot issues
- Investigating strange behavior
- Analyzing game flow in detail
- Preparing detailed bug reports
- Testing new features

**Example:**
```
Double-click debug-enable.bat
→ Confirms enablement
→ Updates .env file
→ Asks to restart bot
→ Debug logs start appearing in debug-logs/
```

**Output location:** `debug-logs\stronglink-debug-YYYY-MM-DD.log`

**Warning:** Debug mode increases disk usage and may slightly impact performance.

---

### debug-disable.bat - Disable Debug Mode

**What it does:**
- Disables debug logging
- Comments out `DEBUG_MODE=true` in `.env`
- Reverts to normal logging level
- Preserves existing debug log files
- Offers to restart bot

**When to use:**
- After finishing troubleshooting
- To reduce disk usage
- To improve performance
- For normal daily gameplay

**Example:**
```
Double-click debug-disable.bat
→ Confirms disablement
→ Updates .env file
→ Asks to restart bot
→ Normal logging resumes
```

**Note:** Old debug logs are kept in `debug-logs/` directory.

---

### debug-logs-view.bat - View Debug Logs

**What it does:**
- Shows debug log directory contents
- Lists all log files
- Provides multiple viewing options:
  1. Open folder in Explorer
  2. View last 50 lines in console
  3. Open in Notepad
  4. Copy path to clipboard

**When to use:**
- After enabling debug mode
- To analyze detailed bot behavior
- When preparing bug reports
- To investigate specific issues

**Example:**
```
Double-click debug-logs-view.bat
→ Shows log files
→ Choose viewing option
→ Analyze logs
```

**Tips:**
- Search for `[ERR]` to find errors
- Search for player names to track specific users
- Look for timestamps around when issue occurred
- Share relevant log snippets when reporting bugs

---

## 🐛 Debug Mode Details

Debug mode provides comprehensive logging for troubleshooting. See [DEBUG_MODE.md](DEBUG_MODE.md) for complete documentation.

### Quick Debug Workflow

**When you encounter an issue:**

1. **Enable debug mode:**
   ```
   Double-click debug-enable.bat
   Press Y to enable and restart
   ```

2. **Reproduce the issue:**
   - Start a game
   - Perform actions that cause the problem
   - Note the time when it happens

3. **View debug logs:**
   ```
   Double-click debug-logs-view.bat
   Option 2: View last 50 lines
   or
   Option 3: Open in Notepad (search for the time)
   ```

4. **Find relevant logs:**
   - Look for timestamps around the issue
   - Search for `[ERR]` or `[WRN]` markers
   - Find player names or game events
   - Copy the relevant section

5. **Disable debug mode:**
   ```
   Double-click debug-disable.bat
   (After you've captured what you need)
   ```

### What Gets Logged

With debug mode enabled:
- ✅ All player actions
- ✅ Question generation details
- ✅ Answer validation logic
- ✅ Game state changes
- ✅ Sudden death details
- ✅ API requests/responses
- ✅ Error details and stack traces
- ✅ Performance metrics

### Log File Details

- **Location:** `debug-logs/`
- **Format:** `stronglink-debug-YYYY-MM-DD.log`
- **Rotation:** Daily (new file each day)
- **Retention:** 7 days (automatic cleanup)
- **Size limit:** 100MB per file
- **Format:** Timestamped, structured, searchable

---

## 🎯 Common Workflows

### Daily Use (Recommended)

**Just pin `start-fresh.bat` to desktop and double-click it every day:**

```
Double-click start-fresh.bat
→ Always starts with latest code
→ Fresh, clean build
→ Shows logs automatically
```

This is the simplest workflow - one icon on desktop, always works!

---

### Configuration Change

**Change topics or other settings:**

```
1. Edit .env file (change GAME__TOPICS)
2. Double-click restart.bat
3. Done! New configuration active
```

---

### Troubleshooting

**Bot not working? Follow this:**

```
1. Double-click stop.bat
2. Double-click start-fresh.bat
3. Watch logs for errors
```

---

### Getting Update

**New version available on GitHub:**

```
1. Double-click update.bat
2. Press Y to confirm
3. Wait for rebuild
4. Bot restarts with new version
```

---

## 🖥️ Creating Desktop Shortcuts

### Method 1: Right-Click (Easiest)

1. Navigate to `C:\github\strong-link-bot\`
2. Right-click `start-fresh.bat`
3. Send to → Desktop (create shortcut)
4. Rename shortcut to "Start Strong Link Bot"

### Method 2: Drag & Drop

1. Open `C:\github\strong-link-bot\` in Explorer
2. Hold Alt key
3. Drag `start-fresh.bat` to desktop
4. Shortcut created!

### Method 3: Manual Shortcut

1. Right-click on desktop → New → Shortcut
2. Location: `C:\github\strong-link-bot\start-fresh.bat`
3. Name: "Start Strong Link Bot"
4. Click Finish

## 🎨 Customizing Shortcuts

### Change Icon

1. Right-click shortcut → Properties
2. Change Icon button
3. Browse to an .ico file
4. Or use Windows icons: `%SystemRoot%\System32\SHELL32.dll`

### Run Minimized

1. Right-click shortcut → Properties
2. Run: Minimized
3. Starts without showing command window (logs still work)

## 📊 Monitoring & Logs

### View Logs While Bot Runs

```
Double-click logs.bat anytime
→ See real-time activity
→ Press Ctrl+C to exit
```

### Check if Running

```
Double-click status.bat
→ See if container is running
→ Check recent activity
→ Verify data directories
```

### Saved Logs

Logs are also saved to `./logs/` directory:
- View anytime with Notepad
- Persist across restarts
- Useful for debugging

## 🔧 Advanced Usage

### Run in Background (No Console Window)

Create a VBS script to run batch files silently:

**start-silent.vbs:**
```vbs
Set WshShell = CreateObject("WScript.Shell")
WshShell.Run chr(34) & "C:\github\strong-link-bot\start-fresh.bat" & Chr(34), 0
Set WshShell = Nothing
```

Pin this VBS file to desktop for silent startup.

### Scheduled Auto-Start

Use Windows Task Scheduler:

1. Open Task Scheduler
2. Create Basic Task
3. Trigger: At startup or daily
4. Action: Start a program
5. Program: `C:\github\strong-link-bot\start-fresh.bat`
6. Done!

## 🆘 Troubleshooting Scripts

### "Docker is not installed or not running"

**Solution:**
1. Install Docker Desktop: https://www.docker.com/products/docker-desktop
2. Start Docker Desktop
3. Wait for it to fully start (check system tray)
4. Try script again

### ".env file not found"

**Solution:**
1. Run `setup.bat` first
2. Or manually: create `.env` with required configuration
3. Required: `BOT__TOKEN` and `OPENAI__APIKEY`

### "Container exits immediately"

**Solution:**
1. Double-click `logs.bat` to see error
2. Check `.env` file has valid tokens
3. Run `start-fresh.bat` for clean rebuild

### "Port already in use"

**Solution:**
1. Run `stop.bat` first
2. Or check if another bot instance is running
3. Close other applications using same port

## 📁 Directory Structure After Running Scripts

```
C:\github\strong-link-bot\
├── start-fresh.bat ⭐ (Pin this to desktop!)
├── start.bat
├── stop.bat
├── restart.bat
├── logs.bat
├── status.bat
├── update.bat
├── setup.bat
├── .env (your configuration)
├── data/
│   ├── pool/ (question pools)
│   ├── state/ (active games)
│   └── results/ (game history)
└── logs/ (saved logs)
```

## 💡 Pro Tips

1. **Desktop Icon**: Pin `start-fresh.bat` for daily use
2. **Keep Logs Open**: Run `logs.bat` in a separate window to monitor
3. **Daily Updates**: Use `start-fresh.bat` daily to always get latest code
4. **Quick Restart**: Use `restart.bat` after changing `.env`
5. **Backup Data**: Copy `data/` folder periodically to backup game history
6. **Check Status**: Use `status.bat` if unsure if bot is running

## 📞 Support

If scripts aren't working:

1. Check `logs.bat` for errors
2. Run `status.bat` to verify state
3. Try `stop.bat` then `start-fresh.bat`
4. Check Docker Desktop is running
5. Verify `.env` file has correct values

For bot functionality issues, see main `README.md`.

---

## Summary

**For most users, just do this:**

1. First time: Run `setup.bat`
2. Daily use: Pin `start-fresh.bat` to desktop and double-click it
3. Done! Bot always starts with latest code.

That's it! 🎉
