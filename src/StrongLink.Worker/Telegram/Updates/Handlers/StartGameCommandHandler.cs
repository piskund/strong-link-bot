using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class StartGameCommandHandler : CommandHandlerBase
{
    private readonly IGameLifecycleService _lifecycleService;

    public StartGameCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IGameLifecycleService lifecycleService)
        : base(client, localization, repository)
    {
        _lifecycleService = lifecycleService;
    }

    public override string Command => "/begin";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var session = await Repository.LoadAsync(message.Chat.Id, cancellationToken);
        if (session is null)
        {
            return;
        }

        await _lifecycleService.StartGameAsync(session, cancellationToken);
    }
}

