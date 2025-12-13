using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Configuration;

public sealed class BotOptions
{
    public string Token { get; init; } = "";

    public string[] AdminUsernames { get; init; } = Array.Empty<string>();

    public long[] AdminUserIds { get; init; } = Array.Empty<long>();

    public GameLanguage DefaultLanguage { get; init; } = GameLanguage.Russian;

    public QuestionSourceMode QuestionSource { get; init; } = QuestionSourceMode.AI;

    public string StateStoragePath { get; init; } = "data/state";

    public string ResultsStoragePath { get; init; } = "data/results";

    public PollingOptions Polling { get; init; } = new();
}

public sealed class PollingOptions
{
    public bool UseWebhook { get; init; }

    public bool DropPendingUpdates { get; init; } = true;
}

