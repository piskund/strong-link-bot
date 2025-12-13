namespace StrongLink.Worker.Domain;

public sealed class GameSession
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public long ChatId { get; init; }

    public GameLanguage Language { get; set; } = GameLanguage.Russian;

    public QuestionSourceMode QuestionSourceMode { get; set; } = QuestionSourceMode.AI;

    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();

    public int Tours { get; init; }

    public int RoundsPerTour { get; init; }

    public int AnswerTimeoutSeconds { get; init; }

    public int EliminateLowest { get; init; }

    public GameStatus Status { get; set; } = GameStatus.NotConfigured;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public List<Player> Players { get; init; } = new();

    public Queue<long> TurnQueue { get; init; } = new();

    public Dictionary<int, Queue<Question>> QuestionsByTour { get; init; } = new();

    public int CurrentTour { get; set; }

    public int CurrentRound { get; set; }

    public Question? CurrentQuestion { get; set; }

    public long? CurrentPlayerId { get; set; }

    public DateTimeOffset? CurrentQuestionAskedAt { get; set; }

    public Dictionary<string, object> Metadata { get; init; } = new();

    public bool IsPlayerActive(long playerId) => Players.Any(p => p.Id == playerId && p.Status == PlayerStatus.Active);

    public Player? FindPlayer(long playerId) => Players.FirstOrDefault(p => p.Id == playerId);

    public IEnumerable<Player> ActivePlayers => Players.Where(p => p.Status == PlayerStatus.Active);

    public IEnumerable<Player> Alive => Players.Where(p => p.Status is PlayerStatus.Active or PlayerStatus.Pending);
}

