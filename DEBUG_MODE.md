# Debug Mode Guide

Debug mode provides detailed logging for troubleshooting and analysis. When enabled, all debug-level logs are written to files for later review.

## Quick Start

### Enable Debug Mode

**Windows (Scripts):**
```bash
# Double-click debug-enable.bat
# Answer Y to enable and restart
```

**Manual (.env file):**
```env
# Add or uncomment this line in .env
DEBUG_MODE=true
```

Then restart: `docker-compose restart`

### View Debug Logs

**Windows (Scripts):**
```bash
# Double-click debug-logs-view.bat
# Choose option to view logs
```

**Manual:**
```bash
# Logs are in debug-logs directory
dir debug-logs
notepad debug-logs\stronglink-debug-2025-01-18.log
```

### Disable Debug Mode

**Windows (Scripts):**
```bash
# Double-click debug-disable.bat
```

**Manual (.env file):**
```env
# Comment out or set to false
# DEBUG_MODE=false
```

Then restart: `docker-compose restart`

---

## What Debug Mode Does

### When Enabled

✅ **Captures everything:**
- All Debug-level logs from your code
- All Information-level logs
- All Warning and Error logs
- Framework logs (Microsoft, System)
- HTTP requests/responses
- Database operations
- Question generation details
- Player actions
- Game state changes

✅ **Writes to files:**
- Location: `./debug-logs/stronglink-debug-YYYY-MM-DD.log`
- Rolling: New file each day
- Retention: Keeps last 7 days
- Size limit: 100MB per file (then rolls to new file)
- Format: Timestamped, structured, searchable

✅ **Console output:**
- Still shows logs in console/docker logs
- Enhanced format with timestamps and log levels

### When Disabled (Default)

- Only Information, Warning, and Error logs
- No debug-level details
- Console output only (no file logging)
- Lower disk usage
- Slightly better performance

---

## Log File Structure

### File Naming

```
debug-logs/
  stronglink-debug-2025-01-18.log
  stronglink-debug-2025-01-17.log
  stronglink-debug-2025-01-16.log
  ...
```

New file created each day automatically.

### Log Format

Each log entry includes:
```
[2025-01-18 14:32:15.123 +00:00] [DBG] [StrongLink.Worker.Services.GameLifecycleService] Processing answer from player: JohnDoe
```

**Format breakdown:**
- `[2025-01-18 14:32:15.123 +00:00]` - Timestamp with milliseconds and timezone
- `[DBG]` - Log level (DBG, INF, WRN, ERR, FTL)
- `[StrongLink.Worker.Services.GameLifecycleService]` - Source context (class name)
- `Processing answer from player: JohnDoe` - Log message

### Log Levels

| Level | Code | Description | Example |
|-------|------|-------------|---------|
| Debug | DBG | Detailed diagnostic info | "Checking sudden death score: Player1=5, Player2=3" |
| Information | INF | General informational | "Game started with 5 players" |
| Warning | WRN | Unexpected but handled | "Player timeout after 30 seconds" |
| Error | ERR | Error that was caught | "Failed to generate questions: API timeout" |
| Fatal | FTL | Critical failure | "Bot crashed, unhandled exception" |

---

## What Gets Logged in Debug Mode

### Question Generation

```
[DBG] Requesting 30 AI questions for topic "Космос"
[DBG] Prompt: Create trivia questions about Space...
[DBG] OpenAI API call: POST https://api.openai.com/v1/chat/completions
[DBG] OpenAI Response: 200 OK (2.3s)
[DBG] Parsed 35 questions from response
[DBG] Added 35 questions to unused pool
```

### Game Flow

```
[DBG] Turn queue empty. Refilling with 4 active players
[DBG] Starting round 3/10 for tour 2
[DBG] Asking question to player: Alice (ID: 123456)
[DBG] Question: "Which planet is known as the Red Planet?"
[DBG] Expected answer: "Mars"
[DBG] Player Alice answered: "mars"
[DBG] Validating answer with AI (gpt-4o-mini)
[DBG] AI validation result: Correct
[DBG] Score updated: Alice 15 -> 16 points
```

### Sudden Death

```
[INF] Entering sudden death mode for 2 participants: Alice(Score:10), Bob(Score:10)
[DBG] Reset sudden death score for Alice
[DBG] Reset sudden death score for Bob
[DBG] Sudden death initialized. Participant IDs: [123456, 789012], Starting round: 10
[DBG] Checking sudden death progress after round. Participants: 2
[INF] Sudden death score check: Min=1, Max=1, Participants=Alice:1, Bob:1
[INF] Ties still present in sudden death (all scores = 1). Continuing.
[DBG] Checking sudden death progress after round. Participants: 2
[INF] Sudden death score check: Min=1, Max=2, Participants=Alice:1, Bob:2
[INF] Sudden death resolved. Ties broken. Min: 1, Max: 2. Eliminating lowest scorers.
[INF] To eliminate: Alice, Survivors: Bob
```

### Errors and Warnings

```
[WRN] Sudden death round limit reached (10 rounds). Ties remain unresolved.
[ERR] Failed to generate questions: OpenAI API timeout after 60s
[ERR] Exception: HttpRequestException: The request timed out
```

---

## Using Debug Logs for Analysis

### Finding Issues

**Search for errors:**
```bash
# Windows
findstr /C:"[ERR]" debug-logs\stronglink-debug-2025-01-18.log

# PowerShell
Get-Content debug-logs\stronglink-debug-2025-01-18.log | Select-String "[ERR]"

# Linux/Mac
grep "\[ERR\]" debug-logs/stronglink-debug-2025-01-18.log
```

**Find specific player activity:**
```bash
findstr /C:"Player Alice" debug-logs\stronglink-debug-2025-01-18.log
```

**Track a specific game:**
```bash
findstr /C:"Game started" debug-logs\stronglink-debug-2025-01-18.log
```

### Sharing Logs with Support

When reporting issues:

1. **Enable debug mode** (if not already enabled)
2. **Reproduce the issue**
3. **Find the relevant log file** in `debug-logs\`
4. **Copy the relevant section** (timestamps help narrow it down)
5. **Share the log snippet** (via email, GitHub issue, etc.)

**Example:**
```
I encountered sudden death issue at 14:32 on 2025-01-18.
Here are the relevant logs from debug-logs\stronglink-debug-2025-01-18.log:

[2025-01-18 14:32:15.123] [INF] Entering sudden death mode for 2 participants
[2025-01-18 14:32:15.125] [DBG] Reset sudden death score for Alice
[2025-01-18 14:32:15.127] [DBG] Reset sudden death score for Bob
...
```

---

## Performance Impact

### With Debug Mode Enabled

- **CPU**: ~5-10% increase (due to additional logging operations)
- **Memory**: ~50-100MB increase (log buffering)
- **Disk I/O**: Frequent writes to log files
- **Disk Space**: ~10-50MB per day (depends on activity)

### Recommendations

✅ **Enable when:**
- Debugging an issue
- Need to analyze game flow
- Investigating player reports
- Testing new features
- Capturing detailed metrics

❌ **Disable for:**
- Normal daily gameplay
- Production environments with high load
- When disk space is limited
- If performance is critical

---

## Scripts Reference

### Windows Scripts

| Script | Purpose |
|--------|---------|
| `debug-enable.bat` | Enable debug mode |
| `debug-disable.bat` | Disable debug mode |
| `debug-logs-view.bat` | View/manage debug logs |

### Manual Commands

```bash
# Enable
echo DEBUG_MODE=true >> .env
docker-compose restart

# Disable
# Edit .env, comment out or set DEBUG_MODE=false
docker-compose restart

# View logs
cat debug-logs/stronglink-debug-2025-01-18.log
tail -f debug-logs/stronglink-debug-2025-01-18.log  # Follow live

# Clean old logs (older than 7 days)
find debug-logs/ -name "*.log" -mtime +7 -delete  # Linux/Mac
forfiles /P debug-logs /M *.log /D -7 /C "cmd /c del @path"  # Windows
```

---

## Log Rotation

Debug logs automatically rotate:

- **Daily**: New file created at midnight UTC
- **Size-based**: New file if current exceeds 100MB
- **Retention**: Keeps last 7 days automatically
- **Cleanup**: Older files deleted automatically

### Manual Cleanup

If disk space is low:

```bash
# Windows - Delete logs older than 3 days
forfiles /P debug-logs /M *.log /D -3 /C "cmd /c del @path"

# Linux/Mac - Delete logs older than 3 days
find debug-logs/ -name "*.log" -mtime +3 -delete

# Delete all debug logs
rm -rf debug-logs/*.log  # Linux/Mac
del debug-logs\*.log     # Windows
```

---

## Advanced Configuration

### Custom Log Path

Edit `Program.cs` to change log location:

```csharp
.WriteTo.File(
    path: "/custom/path/stronglink-.log",  // Change this
    rollingInterval: RollingInterval.Day,
    // ...
)
```

### Different Log Levels

Edit `appsettings.Debug.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",           // Change to "Information" for less detail
      "Microsoft": "Information",   // Reduce Microsoft framework logs
      "System": "Warning"           // Reduce system logs
    }
  }
}
```

### Longer Retention

Edit `Program.cs`:

```csharp
.WriteTo.File(
    // ...
    retainedFileCountLimit: 30,  // Keep 30 days instead of 7
    // ...
)
```

---

## Troubleshooting

### Debug mode not working

**Check if enabled:**
```bash
# Look for this line in container output
docker-compose logs | findstr "DEBUG MODE"

# Should see:
# 🐛 DEBUG MODE ENABLED - Detailed logs will be written to /app/debug-logs/
```

**Check .env file:**
```bash
cat .env | grep DEBUG_MODE
# Should show: DEBUG_MODE=true
```

**Verify logs directory:**
```bash
ls -la debug-logs/
# Should show .log files
```

### No log files created

1. **Check container is running:**
   ```bash
   docker-compose ps
   ```

2. **Check volume mount:**
   ```bash
   docker inspect stronglink-bot | grep debug-logs
   ```

3. **Check file permissions:**
   ```bash
   ls -la debug-logs/
   # On Linux, ensure directory is writable
   chmod 777 debug-logs/
   ```

### Log files too large

**Reduce log level:**
Edit `.env`:
```env
# Instead of Debug, use Information
LOGGING__LOGLEVEL__DEFAULT=Information
```

**Clear old logs:**
```bash
# Keep only today's log
find debug-logs/ ! -name "*$(date +%Y-%m-%d)*" -name "*.log" -delete
```

---

## Privacy and Security

⚠️ **Debug logs may contain sensitive information:**

- Player Telegram IDs and usernames
- Chat IDs
- Question text
- Player answers
- API requests (but NOT API keys)
- Internal game state

### Best Practices

✅ **DO:**
- Review logs before sharing
- Redact sensitive information when posting publicly
- Keep debug logs secure
- Delete logs when no longer needed

❌ **DON'T:**
- Share raw logs publicly without review
- Include API keys or tokens in logs (code already prevents this)
- Store logs indefinitely

---

## Summary

**Debug mode is a powerful tool for troubleshooting and analysis.**

- ✅ **Easy to enable**: Just set `DEBUG_MODE=true` in `.env`
- ✅ **Comprehensive logs**: Everything is captured
- ✅ **Automatic rotation**: No manual cleanup needed
- ✅ **Searchable**: Easy to find specific events
- ✅ **Reversible**: Disable anytime

**When to use:**
- Debugging issues
- Analyzing game behavior
- Investigating player reports
- Testing changes

**When to disable:**
- Normal gameplay
- Production use
- Limited disk space

For most users: **Enable when needed, disable when done.**

---

## Support

Need help with debug logs?

1. Check this guide
2. Run `debug-logs-view.bat` to inspect logs
3. Search logs for errors: `findstr "[ERR]" debug-logs\*.log`
4. Share relevant log snippets when reporting issues

**Contact:** dmytro.piskun@gmail.com
