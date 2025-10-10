using Microsoft.Extensions.Logging;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class PreparePoolCommandHandler : CommandHandlerBase
{
    private readonly ILogger<PreparePoolCommandHandler> _logger;
    private readonly QuestionProviderFactory _factory;

    public PreparePoolCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        QuestionProviderFactory factory,
        ILogger<PreparePoolCommandHandler> logger)
        : base(client, localization, repository)
    {
        _factory = factory;
        _logger = logger;
    }

    public override string Command => "/prepare_pool";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            return;
        }

        if (session.Players.Count == 0)
        {
            var emptyText = Localization.GetString(session.Language, "Bot.NoPlayers");
            await Client.SendTextMessageAsync(chatId, emptyText, cancellationToken: cancellationToken);
            return;
        }

        var preparing = Localization.GetString(session.Language, "Bot.PoolPreparing");
        await Client.SendTextMessageAsync(chatId, preparing, cancellationToken: cancellationToken);

        try
        {
            var provider = _factory.Resolve(session.QuestionSourceMode);
            var pool = await provider.PrepareQuestionPoolAsync(
                session.Topics,
                session.Tours,
                session.RoundsPerTour,
                session.Players,
                session.Language,
                cancellationToken);

            session.QuestionsByTour.Clear();
            foreach (var (tour, questions) in pool)
            {
                session.QuestionsByTour[tour] = new Queue<Question>(questions);
            }

            session.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(session, cancellationToken);

            var readyText = Localization.GetString(session.Language, "Bot.PoolReady");
            await Client.SendTextMessageAsync(chatId, readyText, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare question pool for chat {ChatId}", chatId);
            var failure = string.Format(Localization.GetString(session.Language, "Bot.PoolFailure"), ex.Message);
            await Client.SendTextMessageAsync(chatId, failure, cancellationToken: cancellationToken);
        }
    }
}

