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
cp StrongLink.Worker/env.template .env
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
    "DefaultLanguage": "ru",
    "QuestionSource": "AI"
  },
  "Game": {
    "Tours": 8,
    "RoundsPerTour": 10,
    "AnswerTimeoutSeconds": 30,
    "EliminateLowest": 1
  }
}
```

### 4. Run the Bot

```bash
dotnet run --project StrongLink.Worker
```

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

### Primary Commands
- `/start` – Initialize game session and announce setup instructions
- `/join` – Players join the pending game lobby
- `/prepare_pool` – Generate questions with OpenAI (admin only)
- `/fetch_pool` – Download questions from the ChGK database (admin only)
- `/begin` – Start the tournament once the pool is ready (admin only)
- `/standings` – Show live leaderboard and statuses
- `/help` – Detailed help and command summary
- `/stop` – Cancel the current game (admin only)

### Gameplay Flow
1. Admin runs `/start` to prepare the lobby
2. Players opt in with `/join`
3. Admin prepares questions via `/prepare_pool` (AI) or `/fetch_pool` (ChGK)
4. Admin launches the game with `/begin`
5. Strong Link rotates through players, sends questions, scores answers, and eliminates low scorers after each tour
6. Upon completion, the bot announces winners and exports results to JSON storage

## Configuration Options

| Section | Option | Description | Default |
|---------|--------|-------------|---------|
| `Bot` | `DefaultLanguage` | `ru` or `en` | `ru` |
| `Bot` | `QuestionSource` | `AI` or `Chgk` | `AI` |
| `Game` | `Tours` | Total tours per tournament | `8` |
| `Game` | `RoundsPerTour` | Rounds (full player rotations) per tour | `10` |
| `Game` | `EliminateLowest` | Players removed after each tour | `1` |
| `Game` | `Topics` | Optional custom topics per tour | Default set |
| `OpenAi` | `Model` | OpenAI chat model | `gpt-4o-mini` |
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

**Game won’t start:**
- Make sure at least two players joined via `/join`
- Confirm a question pool was prepared (`/prepare_pool` or `/fetch_pool`)

## Privacy and Ethics
- Only stores minimal game state for active tournaments
- Results exports saved locally under `data/results`
- Designed for group entertainment and educational purposes
- No personal data persists beyond gameplay metadata

## Author

**Dmytro Piskun**  
📧 Contact: [dmytro.piskun@gmail.com](mailto:dmytro.piskun@gmail.com)

## License

MIT License — see [LICENSE](LICENSE) for details.

## Disclaimer

Strong Link is provided “as-is” for community quiz experiences. AI-generated questions may contain inaccuracies—verify content before using in formal competitions. Use responsibly and respect Telegram Terms of Service.
