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
        _logger.LogInformation("User {Username} ({UserId}) issued /fetch_pool command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}", chatId);
            return;
        }

        // Use at least 3 players when calculating required questions
        var effectivePlayerCount = Math.Max(3, session.Players.Count);
        var requiredPerTour = effectivePlayerCount * session.RoundsPerTour;
        _logger.LogInformation("Fetching questions from ChGK: {EffectiveCount} players * {Rounds} rounds = {Required} per tour",
            effectivePlayerCount, session.RoundsPerTour, requiredPerTour);

        var preparing = Localization.GetString(session.Language, "Bot.PoolPreparing");
        await Client.SendTextMessageAsync(chatId, preparing, cancellationToken: cancellationToken);

        try
        {
            // NEW STRATEGY: Fetch only 1 tour from ChGK, just like AI generation
            var provider = _factory.Resolve(QuestionSourceMode.Chgk);

            // Use first topic or generic topic
            var topic = session.Topics.FirstOrDefault() ?? "General";
            var fetchingMessage = session.Language == GameLanguage.Russian
                ? $"🔄 Загружаю вопросы из базы ЧГК для 1 тура: \"{topic}\"..."
                : $"🔄 Fetching questions from ChGK database for 1 tour: \"{topic}\"...";

            await Client.SendTextMessageAsync(chatId, fetchingMessage, cancellationToken: cancellationToken);

            var pool = await provider.PrepareQuestionPoolAsync(
                new[] { topic },
                1,  // Fetch only 1 tour ahead
                requiredPerTour,
                session.Players,
                session.Language,
                cancellationToken);

            var fetchedQuestions = pool.Values.FirstOrDefault() ?? new List<Question>();
            _logger.LogInformation("Fetched {Count} questions from ChGK for topic '{Topic}'",
                fetchedQuestions.Count, topic);

            // Set the question source mode and prepare for first tour
            session.QuestionSourceMode = QuestionSourceMode.Chgk;
            session.QuestionsByTour.Clear();

            if (fetchedQuestions.Count > 0)
            {
                session.QuestionsByTour[1] = new Queue<Question>(fetchedQuestions.Take(requiredPerTour));
            }

            session.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(session, cancellationToken);

            var readyText = session.Language == GameLanguage.Russian
                ? $"✅ Загружено {fetchedQuestions.Count} вопросов из базы ЧГК для первого тура.\n" +
                  $"Вопросы для следующих туров будут загружены автоматически во время игры.\n" +
                  $"Используйте /begin для старта игры."
                : $"✅ Fetched {fetchedQuestions.Count} questions from ChGK database for the first tour.\n" +
                  $"Questions for next tours will be fetched automatically during gameplay.\n" +
                  $"Use /begin to start the game.";

            await Client.SendTextMessageAsync(chatId, readyText, cancellationToken: cancellationToken);
            _logger.LogInformation("ChGK question pool prepared successfully for chat {ChatId}. Fetched {Fetched} questions",
                chatId, fetchedQuestions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch question pool for chat {ChatId}", chatId);
            var failure = string.Format(Localization.GetString(session.Language, "Bot.PoolFailure"), ex.Message);
            await Client.SendTextMessageAsync(chatId, failure, cancellationToken: cancellationToken);
        }
    }
}

