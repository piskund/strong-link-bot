using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class FetchPoolCommandHandler : CommandHandlerBase
{
    private readonly ILogger<FetchPoolCommandHandler> _logger;
    private readonly QuestionProviderFactory _factory;

    public FetchPoolCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        QuestionProviderFactory factory,
        ILogger<FetchPoolCommandHandler> logger,
        IOptions<BotOptions> botOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _factory = factory;
        _logger = logger;
    }

    public override string Command => "/fetch_pool";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            return;
        }

        var preparing = Localization.GetString(session.Language, "Bot.PoolPreparing");
        await Client.SendTextMessageAsync(chatId, preparing, cancellationToken: cancellationToken);

        try
        {
            var provider = _factory.Resolve(QuestionSourceMode.Chgk);
            var pool = await provider.PrepareQuestionPoolAsync(
                session.Topics,
                session.Tours,
                session.RoundsPerTour,
                session.Players,
                session.Language,
                cancellationToken);

            session.QuestionSourceMode = QuestionSourceMode.Chgk;
            session.QuestionsByTour.Clear();
            foreach (var (tour, questions) in pool)
            {
                session.QuestionsByTour[tour] = new Queue<Question>(questions);
            }

            session.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(session, cancellationToken);

            var ready = Localization.GetString(session.Language, "Bot.PoolReady");
            await Client.SendTextMessageAsync(chatId, ready, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch question pool for chat {ChatId}", chatId);
            var failure = string.Format(Localization.GetString(session.Language, "Bot.PoolFailure"), ex.Message);
            await Client.SendTextMessageAsync(chatId, failure, cancellationToken: cancellationToken);
        }
    }
}

