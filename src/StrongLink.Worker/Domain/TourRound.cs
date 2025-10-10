namespace StrongLink.Worker.Domain;

public sealed record TourRound
{
    public required int TourNumber { get; init; }

    public required int RoundNumber { get; init; }

    public required string Topic { get; init; }

    public required IReadOnlyList<Question> Questions { get; init; }
}

