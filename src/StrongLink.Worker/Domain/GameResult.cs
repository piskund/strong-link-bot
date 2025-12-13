namespace StrongLink.Worker.Domain;

/// <summary>
/// Represents the final result of a completed or stopped game for archival purposes.
/// </summary>
public sealed class GameResult
{
    public required string GameId { get; init; }

    public required long ChatId { get; init; }

    public required GameLanguage Language { get; init; }

    public required QuestionSourceMode QuestionSourceMode { get; init; }

    public required string[] Topics { get; init; }

    public required int Tours { get; init; }

    public required int RoundsPerTour { get; init; }

    public required GameStatus FinalStatus { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Final player standings with scores and statistics.
    /// </summary>
    public required List<PlayerResult> Players { get; init; }

    /// <summary>
    /// Questions that were asked during the game.
    /// </summary>
    public required List<Question> UsedQuestions { get; init; }

    /// <summary>
    /// Statistics about the game.
    /// </summary>
    public required GameStatistics Statistics { get; init; }
}

public sealed class PlayerResult
{
    public required long Id { get; init; }

    public required string DisplayName { get; init; }

    public required int Score { get; init; }

    public required int CorrectAnswers { get; init; }

    public required int IncorrectAnswers { get; init; }

    public required PlayerStatus FinalStatus { get; init; }

    /// <summary>
    /// Final placement (1 = winner, 2 = second place, etc.)
    /// </summary>
    public int? Placement { get; init; }
}

public sealed class GameStatistics
{
    public required int TotalQuestions { get; init; }

    public required int ToursCompleted { get; init; }

    public required int PlayersStarted { get; init; }

    public required int PlayersEliminated { get; init; }

    public required int PlayersFinished { get; init; }

    public required double AverageScore { get; init; }

    public required double AverageAccuracy { get; init; }
}
