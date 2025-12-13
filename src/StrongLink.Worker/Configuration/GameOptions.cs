namespace StrongLink.Worker.Configuration;

public sealed class GameOptions
{
    public int Tours { get; init; } = 8;

    public int RoundsPerTour { get; init; } = 10;

    public int AnswerTimeoutSeconds { get; init; } = 30;

    public int EliminateLowest { get; init; } = 1;

    public string[] Topics { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Enable AI-powered answer validation for more flexible matching.
    /// When enabled, uses OpenAI to check if answers are semantically correct
    /// even with minor spelling differences, word order variations, etc.
    /// </summary>
    public bool UseAiAnswerValidation { get; init; } = true;
}

