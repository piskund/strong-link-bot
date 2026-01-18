# Strong Link 🏆

A Telegram quiz bot inspired by the classic “Weakest Link” format. Strong Link runs full-length tournaments in group chats, guiding players through themed tours, timed rounds, score tracking, and eliminations until champions emerge.

## Features

- **Structured Quiz Tournaments**: Configurable tours with 10 rounds each. Games naturally end through elimination mechanics when a winner emerges, with a safety limit to prevent infinite games
- **Multi-language Play**: Russian (default) and English interfaces, with localized prompts and help
- **Flexible Question Sources**: AI-generated trivia via OpenAI or curated packs fetched from the ЧГК (ChGK) database
- **Rich Media Questions**: Configurable image support with reliable CDN sources (Unsplash, Wikimedia, Pixabay, Pexels) or optional DALL-E generation
- **Persistent Game State**: JSON-backed storage for state recovery and exporting final results
- **Telegram-native UX**: Command-driven controls, auto-messaging, and real-time scoreboard updates in group chats

## Quick Start

### 1. Prerequisites

- .NET SDK 9.0+
- Telegram Bot Token (from [@BotFather](https://t.me/botfather))
- Optional: OpenAI API key for AI question mode

### 2. Installation

```bash
# Clone the repository
git clone https://github.com/your-username/strong-link-bot
cd strong-link-bot

# Restore dependencies
dotnet restore
```

### 3. Configuration

1. Copy the environment template:

```bash
copy env_template.txt .env
```

2. Update `.env` with your keys:

```env
TELEGRAM_BOT_TOKEN=your_bot_token_here
OPENAI_API_KEY=your_openai_key_here # optional if using AI questions
```

3. Configure runtime options with `appsettings.json` as needed:

```json
{
  "Bot": {
    "AdminUserIds": [123456789],
    "AdminUsernames": ["your_username"],
    "DefaultLanguage": "ru",
    "QuestionSource": "AI"
  },
  "Game": {
    "Tours": 999,  // Safety limit - games normally end when 1 player remains
    "RoundsPerTour": 10,
    "AnswerTimeoutSeconds": 30,
    "EliminateLowest": 1,
    "Topics": ["Эротика", "Литература", "Фильмы", "Фантастика", "Анекдоты"]
  },
  "OpenAi": {
    "Model": "gpt-5.2",
    "AnswerValidationModel": "gpt-4o-mini"
  }
}
```

**Admin Authorization:**
Admin commands (/start, /begin, /stop, /prepare_pool, etc.) can be executed by:
1. **Telegram Group Owners and Admins** - Any user who is an owner or administrator of the Telegram group where the bot is added
2. **Configured Admins** - Specific users listed in appsettings.json:
   - `AdminUserIds`: Array of Telegram user IDs (recommended, more secure)
   - `AdminUsernames`: Array of Telegram usernames without @ prefix (fallback)

The bot automatically checks if a user is a group owner/admin using Telegram's built-in permissions. This means you don't need to manually configure every admin - just add the bot to your group and promote users as needed.

If both arrays are empty and the bot cannot verify Telegram group admin status, all users can execute admin commands (useful for testing). User ID is more reliable than username since usernames can change.

**Configuration Precedence:**
Strong Link uses multiple configuration sources with the following precedence (later sources override earlier ones):

1. `appsettings.json` - Default values
2. `appsettings.Development.json` - Development-specific values (if exists)
3. `.env` file - Environment variables (use `SECTION__PROPERTY` format, e.g., `OPENAI__MODEL=gpt-5.2`)
4. System environment variables
5. Command line arguments

**Example:** If you set `"Model": "gpt-4o-mini"` in `appsettings.json` but also set `OPENAI__MODEL=gpt-5.2` in `.env`, the bot will use `gpt-5.2`.

### 4. Run the Bot

#### Option A: Run with .NET (Development)

```bash
dotnet run --project src/StrongLink.Worker
```

#### Option B: Run with Docker (Production)

**Prerequisites:**
- Docker Desktop, Rancher Desktop, or Docker Engine installed

**Windows Users - Easy Desktop Scripts (Recommended):**

1. **First time setup:**
   ```bash
   # Double-click setup.bat - it will guide you through configuration
   ```

2. **Daily use:**
   ```bash
   # Pin start-fresh.bat to your desktop and double-click it
   # It always pulls latest code and rebuilds for a fresh start
   ```

See [SCRIPTS_GUIDE.md](SCRIPTS_GUIDE.md) for all available scripts (start, stop, logs, update, etc.)

**Manual Docker Commands (All Platforms):**

**1. Build and run with Docker Compose (Recommended):**

```bash
# Make sure you have a .env file in the root directory with your tokens
docker-compose up -d
```

**2. Or build and run manually:**

```bash
# Build the Docker image
docker build -t stronglink-bot .

# Run the container
docker run -d \
  --name stronglink-bot \
  --env-file .env \
  -v ./data/results:/app/data/results \
  -v ./logs:/app/logs \
  --restart unless-stopped \
  stronglink-bot
```

**3. Manage the container:**

```bash
# View logs
docker-compose logs -f
# or
docker logs -f stronglink-bot

# Stop the bot
docker-compose down
# or
docker stop stronglink-bot

# Restart the bot
docker-compose restart
# or
docker restart stronglink-bot
```

**Docker Configuration:**
- Question pools persisted in `./data/pool` directory
- Active game state persisted in `./data/state` directory
- Game results persisted in `./data/results` directory
- Logs saved to `./logs` directory (optional)
- Container runs as non-root user for security
- Resource limits: 512MB memory, 1 CPU (adjust in docker-compose.yml if needed)

### Standalone Demo (no Telegram required)

```bash
dotnet run --project src/StrongLink.Worker -- --standalone
```

Runs a short 3-tour demo against simulated players (default 45% accuracy).

## Getting Bot Credentials

### Telegram Bot Token
- Message [@BotFather](https://t.me/botfather)
- Run `/newbot` and follow the instructions
- Copy the generated bot token into your `.env`

### OpenAI API Key (AI mode)
- Visit [OpenAI API Keys](https://platform.openai.com/api-keys)
- Create or reuse an API key with sufficient quota
- Update `OPENAI_API_KEY` in `.env`

## Usage

### Admin Commands (Require Authorization)
- `/start` – Initialize game session and announce setup instructions
- `/begin` – Start the tournament once the pool is ready
- `/pause` – Pause the game in progress
- `/resume` – Resume a paused game
- `/stop` – Cancel the current game
- `/prepare_pool` – Generate questions with OpenAI
- `/fetch_pool` – Download questions from the ChGK database
- `/pool_status` – View question pool statistics
- `/pool_clear` – Clear unused questions (use `archive` parameter to clear all)

### Player Commands (Available to All Users)
- `/join` – Join the pending game lobby
- `/standings` – Show live leaderboard and statuses
- `/help` – Detailed help and command summary

### Gameplay Flow
1. Admin runs `/start` to prepare the lobby
2. Players opt in with `/join`
3. Admin prepares questions via `/prepare_pool` (AI) or `/fetch_pool` (ChGK)
4. Admin launches the game with `/begin`
5. Strong Link rotates through players, sends questions, scores answers, and eliminates low scorers after each tour
6. Upon completion, the bot announces winners and automatically finalizes the game

### Game Finalization
When a game ends (either by completion or `/stop` command), Strong Link automatically:
- **Archives game results** to `data/results/` with full statistics, player scores, and used questions
- **Archives used questions** to prevent reuse in future games
- **Clears the session** to allow starting a new game immediately
- **Preserves unused questions** in the pool for future games

You can start a new game right after the previous one ends using the remaining questions in the pool.

## Cost Optimization with Dual Models

Strong Link supports using different OpenAI models for different tasks to optimize cost:

- **Question Generation** (`Model`): Use powerful models like `gpt-5.2` or `gpt-4o` for high-quality, creative questions
- **Answer Validation** (`AnswerValidationModel`): Use cheaper models like `gpt-4o-mini` for simple yes/no validation

**Example configuration:**
```json
"OpenAi": {
  "Model": "gpt-5.2",              // Powerful model for question generation
  "AnswerValidationModel": "gpt-4o-mini"  // Cheap model for answer checking
}
```

This approach significantly reduces costs since:
- Question generation happens once per game (10-80 questions)
- Answer validation happens every player response (potentially hundreds of times)

Using `gpt-4o-mini` for validation instead of `gpt-5.2` can reduce answer validation costs by ~10-20x while maintaining accuracy.

## Image Configuration for AI Questions

Strong Link supports rich media questions with images sourced from reliable CDN providers or generated via DALL-E:

**Automatic Image Sourcing (Recommended)**
When generating AI questions, the bot instructs the AI to use reliable image CDN sources:
- Unsplash (`https://images.unsplash.com/...`)
- Wikimedia Commons direct links (`https://upload.wikimedia.org/...`)
- Pixabay (`https://pixabay.com/get/...`)
- Pexels (`https://images.pexels.com/...`)

Configure the percentage of questions with images using `OPENAI__IMAGEPERCENTAGE` in `.env` (0-100, default: 30).

**DALL-E Image Generation (Optional)**
For questions where the AI doesn't provide images, you can enable automatic image generation via DALL-E:

```env
OPENAI__USEDALLEIMAGEGENERATION=true
OPENAI__DALLEMODEL=dall-e-3
OPENAI__DALLEIMAGESIZE=1024x1024
```

⚠️ **Cost Warning:** DALL-E significantly increases both cost and latency:
- `dall-e-3`: ~$0.04-0.08 per image (depending on size)
- `dall-e-2`: ~$0.016-0.020 per image (cheaper, lower quality)
- Generation time: 5-10 seconds per image

**Recommendation:** Keep `OPENAI__USEDALLEIMAGEGENERATION=false` and let the AI use existing CDN images. Only enable DALL-E if you need custom-generated visuals for every question.

## Configuration Options

| Section | Option | Description | Default |
|---------|--------|-------------|---------|
| `Bot` | `AdminUserIds` | Array of authorized Telegram user IDs | `[]` |
| `Bot` | `AdminUsernames` | Array of authorized Telegram usernames | `[]` |
| `Bot` | `DefaultLanguage` | `ru` or `en` | `ru` |
| `Bot` | `QuestionSource` | `AI` or `Chgk` | `AI` |
| `Game` | `Tours` | Total tours per tournament | `6` |
| `Game` | `RoundsPerTour` | Rounds (full player rotations) per tour | `10` |
| `Game` | `EliminateLowest` | Players removed after each tour | `1` |
| `Game` | `UseAiAnswerValidation` | Use AI for flexible answer checking | `true` |
| `Game` | `DifficultyLevel` | Game difficulty: `Easy` (simple questions, lenient answers), `Medium` (balanced), or `Hard` (complex riddles, strict validation) | `Easy` |
| `Game` | `Topics` | Topic pool for tours. **Configure ONLY in `.env` file** using comma-separated format: `GAME__TOPICS=Topic1,Topic2,Topic3`. Bot uses probability-based selection: 70% chance picks random topic from this list, 30% chance AI generates completely new topic. Topics aren't tied to specific tour numbers. | `["Шахматы", "Космос", "История", "Наука", "Литература", "Фильмы", "Фантастика", "Спорт"]` |
| `OpenAi` | `Model` | OpenAI model for question generation | `gpt-4o-mini` |
| `OpenAi` | `AnswerValidationModel` | OpenAI model for answer validation (optional, uses `Model` if not set) | `gpt-4o-mini` |
| `OpenAi` | `ImagePercentage` | Percentage of questions with images (0-100) | `30` |
| `OpenAi` | `UseDallEImageGeneration` | Generate images via DALL-E for questions without images | `false` |
| `OpenAi` | `DallEModel` | DALL-E model (`dall-e-3` or `dall-e-2`) | `dall-e-3` |
| `OpenAi` | `DallEImageSize` | DALL-E image dimensions (e.g., `1024x1024`) | `1024x1024` |
| `Chgk` | `RandomEndpoint` | Source endpoint for ЧГК questions | `https://db.chgk.info/xml/random` |

## Architecture

```
src/
└── StrongLink.Worker/
    ├── Program.cs                 # Service wiring and DI setup
    ├── Worker.cs                  # Hosted service entry point
    ├── Configuration/             # Options POCOs bound from appsettings
    ├── Domain/                    # Core entities (players, sessions, questions)
    ├── Localization/              # Multi-language resources and helpers
    ├── QuestionProviders/         # AI and ChGK question source strategies
    ├── Persistence/               # JSON-backed game state repository
    ├── Services/                  # Game lifecycle + messaging abstractions
    ├── Telegram/                  # Bot lifetime, dispatcher, command handlers
    └── appsettings*.json          # Runtime configuration defaults

tests/
└── StrongLink.Worker.Tests/
    ├── GameLifecycleServiceTests.cs        # Messaging and scoring flow tests
    ├── QuestionProviders/                  # AI provider parsing tests
    └── StrongLink.Worker.Tests.csproj      # xUnit test project
```

## Debug Mode

For detailed troubleshooting and analysis, enable debug mode to capture comprehensive logs:

```bash
# Windows: Double-click debug-enable.bat
# Manual: Add to .env file
DEBUG_MODE=true
```

Debug logs are saved to `debug-logs/` directory with detailed information about:
- Player actions and game flow
- Question generation details
- Answer validation logic
- Error details and stack traces

See [DEBUG_MODE.md](DEBUG_MODE.md) for complete documentation.

## Troubleshooting

**Bot not responding:**
- Verify `TELEGRAM_BOT_TOKEN`
- Confirm the bot is added to the group and privacy mode is disabled
- Check console output for startup errors

**Question preparation fails:**
- Ensure OpenAI key is set when using AI mode
- Confirm network access to `db.chgk.info` when using ChGK mode
- Review error messages in chat (localized to game language)

**Game won't start:**
- Make sure at least two players joined via `/join`
- Confirm a question pool was prepared (`/prepare_pool` or `/fetch_pool`)

**Admin commands not working:**
- Verify you're an owner or administrator of the Telegram group
- Check that the bot has permission to see group member information
- Alternatively, add your user ID or username to `AdminUserIds` or `AdminUsernames` in appsettings.json
- Get your user ID by messaging [@userinfobot](https://t.me/userinfobot)

## Data Storage

Strong Link maintains several data directories:
- `data/state/` - Active game sessions (automatically cleared after game ends)
- `data/results/` - Archived game results with statistics and player scores
- `data/pool/` - Question pool database (unused and archived questions)

## Privacy and Ethics
- Only stores minimal game state for active tournaments
- Results archives saved locally under `data/results/` with timestamps
- Used questions archived separately to prevent reuse
- Designed for group entertainment and educational purposes
- No personal data persists beyond gameplay metadata (Telegram IDs and display names)

## Author

**Dmytro Piskun**  
📧 Contact: [dmytro.piskun@gmail.com](mailto:dmytro.piskun@gmail.com)

## License

MIT License — see [LICENSE](LICENSE) for details.

## Disclaimer

Strong Link is provided “as-is” for community quiz experiences. AI-generated questions may contain inaccuracies—verify content before using in formal competitions. Use responsibly and respect Telegram Terms of Service.
