namespace StrongLink.Worker.Domain;

public sealed record Player
{
    public required long Id { get; init; }

    public required string DisplayName { get; init; }

    public required PlayerStatus Status { get; set; }

    public int Score { get; set; }

    public int CorrectAnswers { get; set; }

    public int IncorrectAnswers { get; set; }

    // Separate score for sudden death - doesn't count toward total
    public int SuddenDeathScore { get; set; }

    public bool IsEligibleForMedals => Status == PlayerStatus.Active;
}

