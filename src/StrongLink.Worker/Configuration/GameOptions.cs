namespace StrongLink.Worker.Configuration;

public sealed class GameOptions
{
    public int Tours { get; init; } = 8;

    public int RoundsPerTour { get; init; } = 10;

    public int AnswerTimeoutSeconds { get; init; } = 30;

    public int EliminateLowest { get; init; } = 1;

    /// <summary>
    /// Pause duration in seconds between tours. During this pause, the bot shows
    /// current standings, the next tour's topic, and a countdown. Set to 0 to disable pauses.
    /// </summary>
    public int TourPauseSeconds { get; init; } = 60;

    public string[] Topics { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Enable AI-powered answer validation for more flexible matching.
    /// When enabled, uses OpenAI to check if answers are semantically correct
    /// even with minor spelling differences, word order variations, etc.
    /// </summary>
    public bool UseAiAnswerValidation { get; init; } = true;

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
}

