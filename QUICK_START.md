# Quick Start Guide - Strong Link Bot

Get the bot running in 5 minutes! 🚀

## Windows Users (Recommended Method)

### Step 1: First Time Setup (One-time only)

1. Double-click `setup.bat`
2. Edit `.env` when it opens in Notepad:
   - Set `BOT__TOKEN` (get from @BotFather on Telegram)
   - Set `OPENAI__APIKEY` (get from OpenAI platform)
   - Optionally customize `GAME__TOPICS`
3. Save and close Notepad
4. Press Y to build and start the bot

**Done!** The bot is now running.

### Step 2: Create Desktop Shortcut

1. Right-click `start-fresh.bat`
2. Send to → Desktop (create shortcut)
3. Rename to "Start Strong Link Bot"

### Step 3: Daily Use

**Just double-click the desktop icon!**

The `start-fresh.bat` script will:
- Stop old container
- Pull latest code from git
- Rebuild with fresh image
- Start the bot
- Show live logs

**That's it!** 🎉

---

## All Platforms (Manual Docker Commands)

### Step 1: Configure

```bash
# Copy environment template
cp .env.docker .env

# Edit .env and add your tokens
notepad .env  # Windows
nano .env     # Linux/Mac
```

Required values:
- `BOT__TOKEN=your_telegram_bot_token`
- `OPENAI__APIKEY=your_openai_api_key`

### Step 2: Start

```bash
# Build and start
docker-compose up -d

# View logs
docker-compose logs -f
```

### Step 3: Manage

```bash
# Stop
docker-compose down

# Restart
docker-compose restart

# Update and rebuild
git pull
docker-compose down
docker-compose build --no-cache
docker-compose up -d
```

---

## Getting Your Credentials

### Telegram Bot Token

1. Open Telegram and message [@BotFather](https://t.me/botfather)
2. Send `/newbot`
3. Follow the instructions:
   - Choose a name: "Strong Link Quiz"
   - Choose a username: "your_unique_bot"
4. Copy the bot token (looks like: `1234567890:ABCdefGHIjklMNOpqrsTUVwxyz`)
5. Paste into `.env` file: `BOT__TOKEN=1234567890:ABC...`

### OpenAI API Key

1. Visit [OpenAI Platform](https://platform.openai.com/api-keys)
2. Sign up or log in
3. Click "Create new secret key"
4. Name it "StrongLink Bot"
5. Copy the key (starts with `sk-proj-...`)
6. Paste into `.env` file: `OPENAI__APIKEY=sk-proj-...`

⚠️ **Important:** Keep these credentials secret! Never commit `.env` to git.

---

## Adding Bot to Telegram Group

1. Open Telegram
2. Go to your group chat
3. Click group name → Administrators → Add Administrator
4. Search for your bot username
5. Add as administrator (needs permissions to read messages)
6. **Important:** Message @BotFather and disable privacy mode:
   - Send `/setprivacy`
   - Select your bot
   - Click "Disable"

Now the bot can read all messages in the group!

---

## Starting Your First Game

Once the bot is running in your group:

1. An admin sends: `/start`
2. Players join: `/join`
3. Admin prepares questions: `/prepare_pool`
4. Admin starts game: `/begin`

**Game begins!** Players answer questions, scores are tracked, eliminations happen after each tour.

---

## Customizing Topics

Edit `.env` file and change the `GAME__TOPICS` line:

**Russian topics:**
```env
GAME__TOPICS=Шахматы,Космос,История,Наука,Литература,Фильмы,Фантастика,Спорт
```

**English topics:**
```env
GAME__TOPICS=Chess,Space,History,Science,Literature,Movies,Fantasy,Sports
```

**Custom topics:**
```env
GAME__TOPICS=Technology,Art,Music,Architecture,Philosophy,Psychology,Biology,Geography
```

**Restart the bot** to apply changes:
- Windows: Double-click `restart.bat`
- Manual: `docker-compose restart`

---

## Troubleshooting

### Bot not responding in Telegram

1. **Check bot is running:**
   - Windows: Double-click `status.bat`
   - Manual: `docker-compose ps`

2. **Check privacy mode is disabled:**
   - Message @BotFather → `/setprivacy` → Select bot → Disable

3. **Check bot is admin in group:**
   - Group settings → Administrators
   - Your bot should be in the list

### Container exits immediately

1. **Check logs:**
   - Windows: Double-click `logs.bat`
   - Manual: `docker logs stronglink-bot`

2. **Verify .env has correct tokens:**
   - Open `.env` file
   - Make sure `BOT__TOKEN` and `OPENAI__APIKEY` are filled in
   - No spaces, no quotes

3. **Rebuild fresh:**
   - Windows: Double-click `start-fresh.bat`
   - Manual: `docker-compose down && docker-compose build --no-cache && docker-compose up -d`

### "Pool not ready" error

The bot needs questions before starting a game:

1. Run `/prepare_pool` command in the group
2. Wait for generation (30-60 seconds)
3. Then run `/begin` to start

### Questions are in wrong language

1. Edit `.env` file
2. Change `BOT__DEFAULTLANGUAGE`:
   - Russian: `BOT__DEFAULTLANGUAGE=Russian`
   - English: `BOT__DEFAULTLANGUAGE=English`
3. Restart bot

---

## Next Steps

Once you have the bot running:

- 📖 Read [README.md](README.md) for full command list
- 🐳 Read [DOCKER.md](DOCKER.md) for advanced Docker configuration
- 📝 Read [SCRIPTS_GUIDE.md](SCRIPTS_GUIDE.md) for all available scripts (Windows)
- ⚙️ Read [TOPICS_CONFIGURATION.md](TOPICS_CONFIGURATION.md) for topic customization

---

## Windows Scripts Quick Reference

| Script | Purpose |
|--------|---------|
| `setup.bat` | First-time setup |
| `start-fresh.bat` ⭐ | Daily use - always fresh |
| `start.bat` | Quick start (no rebuild) |
| `stop.bat` | Stop the bot |
| `restart.bat` | Restart (apply .env changes) |
| `logs.bat` | View live logs |
| `status.bat` | Check if running |
| `update.bat` | Pull latest from git |

**Recommendation:** Pin `start-fresh.bat` to your desktop for daily use.

---

## Support

Need help?

- Check [README.md](README.md) for detailed documentation
- Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) if it exists
- Review logs: `logs.bat` or `docker-compose logs`
- Check bot status: `status.bat` or `docker-compose ps`

**Author:** Dmytro Piskun
**Email:** dmytro.piskun@gmail.com

---

**That's it!** You now have a fully functional Strong Link quiz bot running in your Telegram group. Have fun! 🎉🏆
