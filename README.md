# Strong Link 🏆

A Telegram quiz bot inspired by the classic “Weakest Link” format. Strong Link runs full-length tournaments in group chats, guiding players through themed tours, timed rounds, score tracking, and eliminations until champions emerge.

## Features

- **Structured Quiz Tournaments**: 8 configurable tours with 10 rounds each, per-spec scoring and elimination mechanics
- **Multi-language Play**: Russian (default) and English interfaces, with localized prompts and help
- **Flexible Question Sources**: AI-generated trivia via OpenAI or curated packs fetched from the ЧГК (ChGK) database
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
    "Tours": 8,
    "RoundsPerTour": 10,
    "AnswerTimeoutSeconds": 30,
    "EliminateLowest": 1
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

```bash
dotnet run --project StrongLink.Worker
```

### Standalone Demo (no Telegram required)

```bash
dotnet run --project StrongLink.Worker -- --standalone
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

## Configuration Options

| Section | Option | Description | Default |
|---------|--------|-------------|---------|
| `Bot` | `AdminUserIds` | Array of authorized Telegram user IDs | `[]` |
| `Bot` | `AdminUsernames` | Array of authorized Telegram usernames | `[]` |
| `Bot` | `DefaultLanguage` | `ru` or `en` | `ru` |
| `Bot` | `QuestionSource` | `AI` or `Chgk` | `AI` |
| `Game` | `Tours` | Total tours per tournament | `8` |
| `Game` | `RoundsPerTour` | Rounds (full player rotations) per tour | `10` |
| `Game` | `EliminateLowest` | Players removed after each tour | `1` |
| `Game` | `UseAiAnswerValidation` | Use AI for flexible answer checking | `true` |
| `Game` | `Topics` | Optional custom topics per tour | Default set |
| `OpenAi` | `Model` | OpenAI model for question generation | `gpt-4o-mini` |
| `OpenAi` | `AnswerValidationModel` | OpenAI model for answer validation (optional, uses `Model` if not set) | `gpt-4o-mini` |
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
