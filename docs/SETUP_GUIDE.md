# Strong Link Bot - Setup & Deployment Guide

## Prerequisites

Before running the bot, ensure you have:

1. **.NET SDK 9.0 or higher** installed
   - Download from: https://dotnet.microsoft.com/download
   - Verify with: `dotnet --version`

2. **Telegram Bot Token** from [@BotFather](https://t.me/botfather)

3. **(Optional) OpenAI API Key** - required only if using AI-generated questions

## Step-by-Step Setup

### Step 1: Create Your Bot with BotFather

1. Open Telegram and search for **@BotFather**
2. Send `/newbot` command
3. Follow the prompts:
   - Choose a name (e.g., "Strong Link Quiz")
   - Choose a username (must end with 'bot', e.g., "stronglink_quiz_bot")
4. **Save the bot token** - it looks like: `1234567890:ABCdefGHIjklMNOpqrsTUVwxyz`

### Step 2: Configure Bot Settings in BotFather

To make the bot work in groups, you must disable privacy mode:

1. In @BotFather, send `/mybots`
2. Select your bot
3. Go to **Bot Settings** → **Group Privacy**
4. Click **Turn off** (this allows the bot to see all messages in group chats)

### Step 3: Add Bot to Your Telegram Channel/Group

1. Create a new Telegram group or open an existing one
2. Add your bot to the group:
   - Click group name → Add members → Search for your bot
   - Or use the bot's username (e.g., @stronglink_quiz_bot)
3. **Make the bot an administrator** (required for full functionality):
   - Group settings → Administrators → Add Administrator
   - Select your bot and grant appropriate permissions

### Step 4: Get OpenAI API Key (Optional)

Only needed if you want AI-generated questions:

1. Visit https://platform.openai.com/api-keys
2. Sign in or create an account
3. Click **"Create new secret key"**
4. Copy the key (starts with `sk-proj-...` or `sk-...`)
5. Ensure you have sufficient credits in your OpenAI account

### Step 5: Configure Environment Variables

1. Navigate to the project directory:
   ```bash
   cd C:\github\strong-link-bot
   ```

2. Create `.env` file in the **project root** (same directory as this guide):
   ```bash
   # Copy the template
   copy env_template.txt .env
   ```

3. Edit `.env` file with your actual credentials:
   ```env
   TELEGRAM_BOT_TOKEN=1234567890:ABCdefGHIjklMNOpqrsTUVwxyz
   OPENAI_API_KEY=sk-proj-your-actual-key-here
   ```

   **IMPORTANT**:
   - Replace `1234567890:ABCdefGHIjklMNOpqrsTUVwxyz` with your actual bot token from BotFather
   - Replace `sk-proj-your-actual-key-here` with your actual OpenAI key
   - If you're NOT using AI questions, you can leave OPENAI_API_KEY empty

### Step 6: Restore Dependencies

```bash
dotnet restore
```

This downloads all required packages for the bot.

### Step 7: Run the Bot

#### Option A: Run Normally (Telegram Mode)

```bash
dotnet run --project src\StrongLink.Worker
```

You should see output like:
```
info: StrongLink.Worker.Worker[0]
      Starting Telegram bot...
info: StrongLink.Worker.Telegram.TelegramBotService[0]
      Bot started successfully: @your_bot_username
```

#### Option B: Test Without Telegram (Standalone Mode)

To test the game logic without Telegram:

```bash
dotnet run --project src\StrongLink.Worker -- --standalone
```

This runs a 3-tour demo with simulated players.

### Step 8: Test Your Bot in Telegram

1. Open your Telegram group where you added the bot
2. Send `/start` - the bot should respond with setup instructions
3. Send `/help` - should show available commands
4. Have at least 2 people send `/join` to join the game
5. Send `/prepare_pool` (AI mode) or `/fetch_pool` (ChGK database mode)
6. Send `/begin` to start the game

## Configuration Options

### Basic Settings (appsettings.json)

Located at: `src\StrongLink.Worker\appsettings.json`

```json
{
  "Bot": {
    "DefaultLanguage": "ru",        // or "en" for English
    "QuestionSource": "AI"          // or "Chgk" for ChGK database
  },
  "Game": {
    "Tours": 8,                     // Number of tournament rounds
    "RoundsPerTour": 10,            // Questions per tour
    "AnswerTimeoutSeconds": 30,     // Time to answer each question
    "EliminateLowest": 1            // Players eliminated per tour
  },
  "OpenAi": {
    "Model": "gpt-4o-mini"          // OpenAI model to use
  }
}
```

## Bot Commands Reference

### Setup Commands
- `/start` - Initialize game session (shows instructions)
- `/join` - Players join the game lobby
- `/prepare_pool` - Generate AI questions (admin only)
- `/fetch_pool` - Fetch questions from ChGK database (admin only)
- `/begin` - Start the tournament (admin only)

### Game Commands
- `/standings` - Show current leaderboard
- `/help` - Show detailed help
- `/stop` - Cancel current game (admin only)

## Typical Game Flow

1. **Admin runs** `/start` → Bot announces game setup
2. **Players send** `/join` → Each player joins the lobby
3. **Admin runs** `/prepare_pool` (or `/fetch_pool`) → Questions are generated/fetched
4. **Admin runs** `/begin` → Game starts
5. **Game plays automatically**:
   - Bot asks questions to each player in rotation
   - Players answer via private messages or in chat
   - Scores are tracked automatically
   - After each tour, lowest scorer is eliminated
6. **Game ends** → Winners announced, results saved to `data/results/`

## Troubleshooting

### Bot doesn't respond to commands

✅ **Checklist:**
- [ ] Bot token is correct in `.env`
- [ ] Bot is added to the group
- [ ] Bot has administrator privileges
- [ ] Privacy mode is DISABLED in @BotFather settings
- [ ] Console shows "Bot started successfully"

### Question preparation fails

✅ **For AI mode:**
- [ ] OpenAI API key is set in `.env`
- [ ] OpenAI account has sufficient credits
- [ ] Internet connection is working

✅ **For ChGK mode:**
- [ ] Internet connection is working
- [ ] https://db.chgk.info is accessible

### Game won't start

✅ **Requirements:**
- [ ] At least 2 players joined via `/join`
- [ ] Question pool prepared via `/prepare_pool` or `/fetch_pool`
- [ ] Admin sent `/begin` command

### Bot crashes or restarts

Check console logs for error messages. Common issues:
- Invalid bot token
- Network connectivity problems
- Insufficient permissions in group

## Running as a Background Service

### Windows (Using Task Scheduler)

1. Create a batch file `run-bot.bat`:
   ```batch
   cd C:\github\strong-link-bot
   dotnet run --project src\StrongLink.Worker
   ```

2. Open Task Scheduler → Create Basic Task
3. Set trigger (e.g., "When computer starts")
4. Action: Start a program → `C:\github\strong-link-bot\run-bot.bat`

### Windows (Using nssm)

1. Download NSSM from https://nssm.cc/download
2. Install service:
   ```bash
   nssm install StrongLinkBot "dotnet" "run --project C:\github\strong-link-bot\src\StrongLink.Worker"
   nssm set StrongLinkBot AppDirectory "C:\github\strong-link-bot"
   nssm start StrongLinkBot
   ```

### Linux (Using systemd)

1. Create service file `/etc/systemd/system/stronglink.service`:
   ```ini
   [Unit]
   Description=Strong Link Telegram Bot
   After=network.target

   [Service]
   Type=simple
   User=your-username
   WorkingDirectory=/path/to/strong-link-bot
   ExecStart=/usr/bin/dotnet run --project /path/to/strong-link-bot/src/StrongLink.Worker
   Restart=always
   RestartSec=10

   [Install]
   WantedBy=multi-user.target
   ```

2. Enable and start:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable stronglink
   sudo systemctl start stronglink
   ```

## Security Best Practices

⚠️ **NEVER commit `.env` to git** - it contains sensitive credentials

✅ The `.env` file is already in `.gitignore`

✅ Keep your tokens secure:
- Don't share bot tokens publicly
- Rotate tokens if accidentally exposed (via @BotFather)
- Keep OpenAI keys private

## Getting Help

- Check console output for error messages
- Review the main [README.md](README.md) for features overview
- For issues, check the troubleshooting section above

## Reference: Working Example (CogniBot)

Your cognibot setup uses:
```env
TELEGRAM_BOT_TOKEN=7969263316:AAG...
TELEGRAM_CHANNELS=cognitivebot_test,@channelname,-1234567890
OPENAI_API_KEY=sk-proj-...
```

Strong Link Bot follows the same pattern but uses:
- Single bot token (not multiple channels)
- Works in group chats where it's added
- Uses same OpenAI key format

---

**Author**: Dmytro Piskun
**Email**: dmytro.piskun@gmail.com
**License**: MIT
