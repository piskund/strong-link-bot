using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

/// <summary>
/// Admin command to manually advance the game to the next round.
/// Used to unstick a game that has no active question (e.g. after a bot restart).
/// </summary>
public sealed class NextRoundCommandHandler : CommandHandlerBase
{
    private readonly IGameLifecycleService _lifecycleService;
    private readonly IChatMessenger _messenger;
    private readonly ILogger<NextRoundCommandHandler> _logger;

    public NextRoundCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IGameLifecycleService lifecycleService,
        IChatMessenger messenger,
        IOptions<BotOptions> botOptions,
        ILogger<NextRoundCommandHandler> logger)
        : base(client, localization, repository, botOptions.Value)
    {
        _lifecycleService = lifecycleService;
        _messenger = messenger;
        _logger = logger;
    }

    public override string Command => "/next";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var session = await Repository.LoadAsync(chatId, cancellationToken);

        if (session is null || (session.Status != GameStatus.InProgress && session.Status != GameStatus.SuddenDeath))
        {
            await _messenger.SendAsync(chatId,
                session?.Language == GameLanguage.Russian
                    ? "⚠️ Нет активной игры для продвижения."
                    : "⚠️ No active game to advance.",
                cancellationToken);
            return;
        }

        if (session.CurrentQuestion is not null)
        {
            await _messenger.SendAsync(chatId,
                session.Language == GameLanguage.Russian
                    ? "⚠️ Вопрос уже задан. Дождитесь ответа или тайм-аута."
                    : "⚠️ A question is already active. Wait for an answer or timeout.",
                cancellationToken);
            return;
        }

        _logger.LogInformation("Admin manually advancing round for chat {ChatId} (tour {Tour}, round {Round})",
            chatId, session.CurrentTour, session.CurrentRound);

        await _lifecycleService.AdvanceRoundAsync(session, cancellationToken);
    }
}
