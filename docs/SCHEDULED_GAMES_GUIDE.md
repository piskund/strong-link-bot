# Scheduled Games Guide

## Overview
The bot supports scheduled games that automatically start at configured times each day. This feature works in both public and private groups.

## Configuration

### 1. Enable Scheduled Games in appsettings.json

```json
{
  "Game": {
    "EnableScheduledGames": true,
    "ScheduledGameTimeUtc": "18:00:00",
    "ScheduledGameWaitMinutes": 10,
    "ScheduledGameChatIds": [-1001234567890, -1009876543210]
  }
}
```

**Configuration Options:**
- `EnableScheduledGames`: Set to `true` to enable the feature
- `ScheduledGameTimeUtc`: Daily time in UTC when games should start (format: "HH:mm:ss")
- `ScheduledGameWaitMinutes`: How long to wait for players before auto-starting
- `ScheduledGameChatIds`: Array of chat IDs where scheduled games should run

### 2. Get Your Chat ID

To find your chat ID:
1. Add the bot to your group
2. Send any message in the group
3. Check the bot logs for a line like: `User X issued /start command in chat -1001234567890`
4. The negative number is your chat ID (groups always have negative IDs)

## Private Groups

**Yes, scheduled games work in private groups!**

### Requirements for Private Groups:
1. Add the bot to your private group
2. Make the bot an administrator (required for sending messages reliably)
3. Add your group's chat ID to `ScheduledGameChatIds` in configuration
4. Restart the bot

### How to Add Bot to Private Group:
1. Open your private group in Telegram
2. Click on group name → "Add Members"
3. Search for your bot username (e.g., @stronglink_bot)
4. Add the bot
5. Make it an admin: Group Settings → Administrators → Add Administrator → Select your bot

## Testing Scheduled Games

### Using /testscheduled Command

Instead of waiting for the scheduled time, use the `/testscheduled` command to test immediately:

```
/testscheduled
```

Or specify custom wait time (in minutes):
```
/testscheduled 5
```

This will:
- Initialize a scheduled game in the current chat
- Set auto-start timer (default: 2 minutes, or specify custom)
- Show the chat ID and auto-start time
- Work exactly like the real scheduled game

**Example Output:**
```
🎮 [TEST] Scheduled game is starting!

Use /join to participate.
The game will automatically begin in 2 minutes if at least 1 player joins.

⏰ Auto-start time: 18:45:30 UTC
📊 Chat ID: -1001234567890
```

## How It Works

1. **At Scheduled Time**: Bot sends a message to configured chats announcing the game
2. **Waiting Period**: Players have X minutes (configured) to join using `/join`
3. **Auto-Start**:
   - If at least 1 player joined → game starts automatically
   - If no players joined → game is cancelled

## User Commands

- `/schedule` - Shows when the next scheduled game will start, or current scheduled game status
- `/testscheduled [minutes]` - (Admin only) Test scheduled game with custom wait time
- `/join` - Join the scheduled game during the waiting period

## Logs

When scheduled games are working correctly, you'll see logs like:

```
info: StrongLink.Worker.Services.ScheduledGameService[0]
      Scheduled game service started. Games will start at 18:00:00 UTC daily in 2 chat(s), with 10 minutes for players to join

info: StrongLink.Worker.Services.ScheduledGameService[0]
      Scheduled game time reached. Triggering scheduled game initialization

info: StrongLink.Worker.Services.ScheduledGameService[0]
      Auto-starting scheduled game in chat -1001234567890 with 3 player(s)
```

## Troubleshooting

### "No chat IDs are configured" Warning
- Add at least one chat ID to `ScheduledGameChatIds` in appsettings.json

### Bot Doesn't Send Messages in Private Group
- Make sure the bot is an administrator in the group
- Check that the chat ID is correct (should be a negative number)
- Verify the bot token is valid

### Scheduled Game Doesn't Start
- Check that `EnableScheduledGames` is `true`
- Verify the chat ID is in `ScheduledGameChatIds`
- Check the bot is running and not crashed
- Look at logs for any errors

### Testing in Development
- Use `/testscheduled` command for quick testing
- Or temporarily set `ScheduledGameTimeUtc` to a few minutes from now

## Example Scenarios

### Single Group, Daily 6 PM UTC
```json
{
  "ScheduledGameTimeUtc": "18:00:00",
  "ScheduledGameWaitMinutes": 10,
  "ScheduledGameChatIds": [-1001234567890]
}
```

### Multiple Groups, Different Wait Times
```json
{
  "ScheduledGameTimeUtc": "20:00:00",
  "ScheduledGameWaitMinutes": 15,
  "ScheduledGameChatIds": [-1001234567890, -1009876543210, -1005555555555]
}
```

### Testing Setup (2 minute wait)
Use `/testscheduled 2` in your group to start a test game with 2-minute wait time.
