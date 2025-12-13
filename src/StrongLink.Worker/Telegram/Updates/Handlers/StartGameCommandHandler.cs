using Microsoft.Extensions.Logging;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class StartGameCommandHandler : CommandHandlerBase
{
    private readonly IGameLifecycleService _lifecycleService;
    private readonly ILogger<StartGameCommandHandler> _logger;

    public StartGameCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IGameLifecycleService lifecycleService,
        ILogger<StartGameCommandHandler> logger)
        : base(client, localization, repository)
    {
        _lifecycleService = lifecycleService;
        _logger = logger;
    }

    public override string Command => "/begin";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("User {Username} ({UserId}) issued /begin command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, message.Chat.Id);

        var session = await Repository.LoadAsync(message.Chat.Id, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}", message.Chat.Id);
            return;
        }

        _logger.LogInformation("Session found. Status: {Status}, Players: {PlayerCount}, Questions: {QuestionCount}",
            session.Status, session.Players.Count, session.QuestionsByTour.Values.Sum(q => q.Count));

        await _lifecycleService.StartGameAsync(session, cancellationToken);
    }
}

