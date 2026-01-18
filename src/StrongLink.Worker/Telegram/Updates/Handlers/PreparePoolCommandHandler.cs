using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class PreparePoolCommandHandler : CommandHandlerBase
{
    private readonly ILogger<PreparePoolCommandHandler> _logger;
    private readonly QuestionProviderFactory _factory;
    private readonly IQuestionPoolRepository _poolRepository;

    public PreparePoolCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        QuestionProviderFactory factory,
        IQuestionPoolRepository poolRepository,
        ILogger<PreparePoolCommandHandler> logger,
        IOptions<BotOptions> botOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _factory = factory;
        _poolRepository = poolRepository;
        _logger = logger;
    }

    public override string Command => "/prepare_pool";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /prepare_pool command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}", chatId);
            return;
        }

        _logger.LogInformation("Preparing question pool for chat {ChatId}. Source: {Source}, Tours: {Tours}, Rounds: {Rounds}, Players: {PlayerCount}",
            chatId, session.QuestionSourceMode, session.Tours, session.RoundsPerTour, session.Players.Count);

        try
        {
            // Use at least 3 players when calculating required questions
            // This ensures enough questions even if players join later
            var effectivePlayerCount = Math.Max(3, session.Players.Count);
            var requiredPerTour = effectivePlayerCount * session.RoundsPerTour;
            _logger.LogInformation("Calculating required questions: {EffectiveCount} players * {Rounds} rounds = {Required} per tour",
                effectivePlayerCount, session.RoundsPerTour, requiredPerTour);

            // Check if there are unused questions in the pool
            var poolStats = await _poolRepository.GetPoolStatsAsync(cancellationToken);
            _logger.LogInformation("Current pool stats: {Unused} unused, {Archived} archived",
                poolStats.Unused, poolStats.Archived);

            // NEW STRATEGY: Only generate 1 tour ahead, and only if there are NO unused questions
            if (poolStats.Unused >= requiredPerTour)
            {
                var infoMessage = session.Language == GameLanguage.Russian
                    ? $"✅ В пуле уже есть {poolStats.Unused} неиспользованных вопросов (нужно {requiredPerTour} на тур).\n" +
                      $"Генерация не требуется. Используйте /begin для старта игры."
                    : $"✅ Pool already has {poolStats.Unused} unused questions (need {requiredPerTour} per tour).\n" +
                      $"No generation needed. Use /begin to start the game.";

                await Client.SendTextMessageAsync(chatId, infoMessage, cancellationToken: cancellationToken);
                _logger.LogInformation("Skipping generation - pool has {Unused} unused questions, need {Required} per tour",
                    poolStats.Unused, requiredPerTour);

                session.Status = GameStatus.ReadyToStart;
                await Repository.SaveAsync(session, cancellationToken);
                return;
            }

            // Get archived questions to avoid repetition when generating new ones
            var archivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(cancellationToken);
            _logger.LogInformation("Retrieved {Count} archived questions for AI context", archivedQuestions.Count);

            // Generate only 1 tour ahead with probability-based topic selection
            var preparing = Localization.GetString(session.Language, "Bot.PoolPreparing");
            await Client.SendTextMessageAsync(chatId, preparing, cancellationToken: cancellationToken);

            // Select topic with 70% from Topics list, 30% random
            var selectedTopic = AiQuestionProvider.SelectTopicWithProbability(session.Topics);
            var isRandomTopic = string.IsNullOrEmpty(selectedTopic);
            var topicDisplay = isRandomTopic
                ? (session.Language == GameLanguage.Russian ? "случайная тема" : "random topic")
                : selectedTopic;

            _logger.LogInformation("Selected topic for generation: '{Topic}' (random: {IsRandom})",
                topicDisplay, isRandomTopic);

            var generatingMessage = session.Language == GameLanguage.Russian
                ? $"🤖 Генерирую вопросы для 1 тура: \"{topicDisplay}\"..."
                : $"🤖 Generating questions for 1 tour: \"{topicDisplay}\"...";

            await Client.SendTextMessageAsync(chatId, generatingMessage, cancellationToken: cancellationToken);

            var provider = _factory.Resolve(session.QuestionSourceMode);
            IReadOnlyDictionary<int, List<Question>> generated;

            if (provider is AiQuestionProvider aiProvider)
            {
                generated = await aiProvider.PrepareQuestionPoolAsync(
                    new[] { selectedTopic },
                    1,
                    requiredPerTour, // Generate enough for one full tour
                    session.Players,
                    session.Language,
                    session.MatureContent,
                    archivedQuestions,
                    cancellationToken);
            }
            else
            {
                generated = await provider.PrepareQuestionPoolAsync(
                    new[] { selectedTopic },
                    1,
                    requiredPerTour,
                    session.Players,
                    session.Language,
                    session.MatureContent,
                    cancellationToken);
            }

            var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
            _logger.LogInformation("Generated {Count} questions for topic '{Topic}'",
                generatedList.Count, topicDisplay);

            // Add ALL generated questions to the unused pool (not to session)
            // This way they'll be available for any future tours
            if (generatedList.Count > 0)
            {
                await _poolRepository.AddToUnusedPoolAsync(generatedList, cancellationToken);
                _logger.LogInformation("Added {Count} generated questions to unused pool", generatedList.Count);
            }

            // Questions are now in the unused pool, ready to be used by PrepareNextTourQuestionsAsync
            // Mark session as ready to start
            session.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(session, cancellationToken);

            var readyText = session.Language == GameLanguage.Russian
                ? $"✅ Пул готов! Добавлено {generatedList.Count} новых вопросов в общий пул.\n" +
                  $"Вопросы будут автоматически выбираться для каждого тура во время игры.\n" +
                  $"Используйте /begin для старта игры."
                : $"✅ Pool ready! Added {generatedList.Count} new questions to the general pool.\n" +
                  $"Questions will be automatically selected for each tour during gameplay.\n" +
                  $"Use /begin to start the game.";

            await Client.SendTextMessageAsync(chatId, readyText, cancellationToken: cancellationToken);
            _logger.LogInformation("Question pool prepared successfully for chat {ChatId}. Generated {Generated} questions, added to unused pool",
                chatId, generatedList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare question pool for chat {ChatId}", chatId);
            var failure = string.Format(Localization.GetString(session.Language, "Bot.PoolFailure"), ex.Message);
            await Client.SendTextMessageAsync(chatId, failure, cancellationToken: cancellationToken);
        }
    }
}

