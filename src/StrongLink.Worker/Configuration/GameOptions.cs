using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Configuration;

public sealed class GameOptions
{
    /// <summary>
    /// Maximum number of tours as a safety limit to prevent infinite games.
    /// In practice, games end naturally when only 1 player remains through elimination.
    /// This high default (999) ensures games end based on elimination logic, not tour count.
    /// </summary>
    public int Tours { get; init; } = 999;

    public int RoundsPerTour { get; init; } = 10;

    public int AnswerTimeoutSeconds { get; init; } = 30;

    public int EliminateLowest { get; init; } = 1;

    /// <summary>
    /// Pause duration in seconds between tours. During this pause, the bot shows
    /// current standings, the next tour's topic, and prepares questions for the next tour.
    /// Set to 0 to disable pauses.
    /// </summary>
    public int TourPauseSeconds { get; init; } = 30;

    /// <summary>
    /// Recommended topics for tours. These are suggestions for AI to use.
    /// If there are fewer topics than tours, AI will generate random topics for remaining tours.
    /// </summary>
    public string[] Topics { get; set; } = [ "Шахматы", "Литература", "Фильмы", "Фантастика", "История", "Наука", "Животные", "Растения", "Спорт", "Космос"];

    /// <summary>
    /// Enable AI-powered answer validation for more flexible matching.
    /// When enabled, uses OpenAI to check if answers are semantically correct
    /// even with minor spelling differences, word order variations, etc.
    /// </summary>
    public bool UseAiAnswerValidation { get; init; } = true;

    /// <summary>
    /// Game difficulty level that affects question complexity and answer validation strictness.
    /// - Easy: Simple questions, lenient answer checking (accepts close answers)
    /// - Medium: Moderate questions, balanced answer checking
    /// - Hard: Complex questions, strict answer checking
    /// </summary>
    public DifficultyLevel DifficultyLevel { get; init; } = DifficultyLevel.Easy;

    /// <summary>
    /// Enable scheduled games that start automatically at a specified time each day.
    /// </summary>
    public bool EnableScheduledGames { get; init; } = false;

    /// <summary>
    /// The time in UTC when scheduled games should start (default: 18:00 / 6 PM UTC).
    /// </summary>
    public TimeSpan ScheduledGameTimeUtc { get; init; } = new TimeSpan(18, 0, 0);

    /// <summary>
    /// Number of minutes to wait for players to join after the scheduled time
    /// before auto-starting the game (default: 10 minutes).
    /// </summary>
    public int ScheduledGameWaitMinutes { get; init; } = 10;

    /// <summary>
    /// List of chat IDs where scheduled games should be enabled.
    /// Leave empty to disable scheduled games for all chats.
    /// </summary>
    public List<long> ScheduledGameChatIds { get; init; } = new();

    /// <summary>
    /// Enable mature content mode (18+) allowing broader topic selection including
    /// mature themes in art, literature, history, mythology, etc.
    /// OpenAI will still filter explicit sexual content per their usage policies.
    /// </summary>
    public bool MatureContentEnabled { get; init; } = true;
}

