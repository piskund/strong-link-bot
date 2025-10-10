using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Configuration;

public sealed class BotOptions
{
    public required string Token { get; init; }

    public string[] AdminUsernames { get; init; } = Array.Empty<string>();

    public GameLanguage DefaultLanguage { get; init; } = GameLanguage.Russian;

    public QuestionSourceMode QuestionSource { get; init; } = QuestionSourceMode.AI;

    public required string StateStoragePath { get; init; }

    public required string ResultsStoragePath { get; init; }

    public PollingOptions Polling { get; init; } = new();
}

public sealed class PollingOptions
{
    public bool UseWebhook { get; init; }

    public bool DropPendingUpdates { get; init; } = true;
}

