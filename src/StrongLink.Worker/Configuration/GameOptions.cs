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
}

