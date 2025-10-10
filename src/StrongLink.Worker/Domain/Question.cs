namespace StrongLink.Worker.Domain;

public sealed record Question
{
    public required string Topic { get; init; }

    public required string Text { get; init; }

    public required string Answer { get; init; }

    public string? SourceId { get; init; }

    public string? SourceName { get; init; }
}

