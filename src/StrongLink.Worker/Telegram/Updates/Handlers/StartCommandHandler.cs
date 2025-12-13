using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class StartCommandHandler : CommandHandlerBase
{
    private readonly ILogger<StartCommandHandler> _logger;
    private readonly BotOptions _botOptions;
    private readonly GameOptions _gameOptions;
    private readonly QuestionProviderFactory _factory;
    private readonly IQuestionPoolRepository _poolRepository;

    public StartCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        QuestionProviderFactory factory,
        IQuestionPoolRepository poolRepository,
        ILogger<StartCommandHandler> logger,
        IOptions<BotOptions> botOptions,
        IOptions<GameOptions> gameOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _factory = factory;
        _poolRepository = poolRepository;
        _logger = logger;
        _botOptions = botOptions.Value;
        _gameOptions = gameOptions.Value;
    }

    public override string Command => "/start";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type is not (ChatType.Group or ChatType.Supergroup))
        {
            await Client.SendTextMessageAsync(message.Chat.Id, "This bot works in group chats only.", cancellationToken: cancellationToken);
            return;
        }

        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /start command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var existingSession = await Repository.LoadAsync(chatId, cancellationToken);
        if (existingSession != null)
        {
            _logger.LogInformation("Existing session found for chat {ChatId}. Status: {Status}, Players: {PlayerCount}",
                chatId, existingSession.Status, existingSession.Players.Count);
        }

        // Randomize topics for variety
        var shuffledTopics = _gameOptions.Topics.OrderBy(_ => Random.Shared.Next()).ToList();

        // Clear completed or cancelled sessions to allow starting a new game
        GameSession session;
        if (existingSession != null && (existingSession.Status == GameStatus.Completed || existingSession.Status == GameStatus.Cancelled))
        {
            _logger.LogInformation("Clearing completed/cancelled session for chat {ChatId}. Starting fresh.", chatId);
            await Repository.RemoveAsync(chatId, cancellationToken);

            session = new GameSession
            {
                ChatId = chatId,
                Language = _botOptions.DefaultLanguage,
                QuestionSourceMode = _botOptions.QuestionSource,
                Topics = shuffledTopics,
                Tours = _gameOptions.Tours,
                RoundsPerTour = _gameOptions.RoundsPerTour,
                AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                EliminateLowest = _gameOptions.EliminateLowest,
                Status = GameStatus.AwaitingPlayers
            };
        }
        else
        {
            session = existingSession ?? new GameSession
            {
                ChatId = chatId,
                Language = _botOptions.DefaultLanguage,
                QuestionSourceMode = _botOptions.QuestionSource,
                Topics = shuffledTopics,
                Tours = _gameOptions.Tours,
                RoundsPerTour = _gameOptions.RoundsPerTour,
                AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                EliminateLowest = _gameOptions.EliminateLowest,
                Status = GameStatus.AwaitingPlayers
            };
        }

        if (session.Status == GameStatus.InProgress || session.Status == GameStatus.SuddenDeath)
        {
            var text = Localization.GetString(session.Language, "Bot.GameAlreadyRunning");
            await Client.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
            return;
        }

        session.Status = GameStatus.AwaitingPlayers;
        await Repository.SaveAsync(session, cancellationToken);

        _logger.LogInformation("Session initialized for chat {ChatId}. Current players: {PlayerCount}",
            chatId, session.Players.Count);

        var welcome = Localization.GetString(session.Language, "Bot.Welcome");
        await Client.SendTextMessageAsync(chatId, welcome, cancellationToken: cancellationToken);

        // Automatically prepare question pool
        await PrepareQuestionPoolAsync(session, cancellationToken);
    }

    private async Task PrepareQuestionPoolAsync(GameSession session, CancellationToken cancellationToken)
    {
        var chatId = session.ChatId;
        _logger.LogInformation("Auto-preparing question pool for chat {ChatId}. Source: {Source}, Tours: {Tours}, Rounds: {Rounds}",
            chatId, session.QuestionSourceMode, session.Tours, session.RoundsPerTour);

        var preparing = Localization.GetString(session.Language, "Bot.PoolPreparing");
        await Client.SendTextMessageAsync(chatId, preparing, cancellationToken: cancellationToken);

        try
        {
            var requiredPerTour = Math.Max(1, session.Players.Count) * session.RoundsPerTour;

            // First, try to get questions from the unused pool
            var poolStats = await _poolRepository.GetPoolStatsAsync(cancellationToken);
            _logger.LogInformation("Current pool stats: {Unused} unused, {Archived} archived",
                poolStats.Unused, poolStats.Archived);

            // Get archived questions to avoid repetition when generating new ones
            var archivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(cancellationToken);
            _logger.LogInformation("Retrieved {Count} archived questions for AI context", archivedQuestions.Count);

            var pool = new Dictionary<int, List<Question>>();
            var reusedCount = 0;
            var generatedQuestions = new List<Question>();

            // OPTIMIZATION: Only prepare Tour 1 immediately for quick start
            for (var tourIndex = 0; tourIndex < 1; tourIndex++)
            {
                var topic = session.Topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
                var questionsFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, cancellationToken);

                if (questionsFromPool.Count >= requiredPerTour)
                {
                    // Enough questions in pool for this tour
                    pool[tourIndex + 1] = questionsFromPool.Take(requiredPerTour)
                        .Select(q => q with { Topic = topic })
                        .ToList();
                    reusedCount += requiredPerTour;
                    _logger.LogDebug("Reusing {Count} questions from pool for tour {Tour} ({Topic})",
                        requiredPerTour, tourIndex + 1, topic);
                }
                else
                {
                    // Not enough in pool, need to generate
                    _logger.LogInformation("Only {Available} questions in pool for tour {Tour}, generating {Needed} more",
                        questionsFromPool.Count, tourIndex + 1, requiredPerTour - questionsFromPool.Count);

                    var provider = _factory.Resolve(session.QuestionSourceMode);

                    // Pass archived questions to AI provider to avoid repetition
                    IReadOnlyDictionary<int, List<Question>> generated;
                    if (provider is AiQuestionProvider aiProvider)
                    {
                        generated = await aiProvider.PrepareQuestionPoolAsync(
                            new[] { topic },
                            1,
                            session.RoundsPerTour,
                            session.Players,
                            session.Language,
                            archivedQuestions,
                            cancellationToken);
                    }
                    else
                    {
                        generated = await provider.PrepareQuestionPoolAsync(
                            new[] { topic },
                            1,
                            session.RoundsPerTour,
                            session.Players,
                            session.Language,
                            cancellationToken);
                    }

                    _logger.LogInformation("Provider returned dictionary with {Count} entries", generated.Count);
                    var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                    _logger.LogInformation("Generated {Count} questions for tour {Tour} topic {Topic}",
                        generatedList.Count, tourIndex + 1, topic);
                    generatedQuestions.AddRange(generatedList);

                    // Combine pool questions + generated
                    var combined = new List<Question>(questionsFromPool);
                    combined.AddRange(generatedList);

                    pool[tourIndex + 1] = combined.Take(requiredPerTour).ToList();
                    _logger.LogInformation("Added {Count} questions to pool dictionary for tour {Tour}",
                        pool[tourIndex + 1].Count, tourIndex + 1);
                    reusedCount += questionsFromPool.Count;
                }
            }

            _logger.LogInformation("Pool dictionary has {Count} tours before copying to session", pool.Count);

            session.QuestionsByTour.Clear();
            foreach (var (tour, questions) in pool)
            {
                session.QuestionsByTour[tour] = new Queue<Question>(questions);
                _logger.LogInformation("Copied {Count} questions to session.QuestionsByTour[{Tour}]", questions.Count, tour);
            }

            // Add only SURPLUS generated questions back to unused pool
            if (generatedQuestions.Count > 0)
            {
                var usedQuestionTexts = new HashSet<string>(
                    pool.Values.SelectMany(q => q).Select(q => q.Text.Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                var surplusQuestions = generatedQuestions
                    .Where(q => !usedQuestionTexts.Contains(q.Text.Trim().ToLowerInvariant()))
                    .ToList();

                if (surplusQuestions.Count > 0)
                {
                    await _poolRepository.AddToUnusedPoolAsync(surplusQuestions, cancellationToken);
                    _logger.LogInformation("Added {Count} surplus generated questions to unused pool (out of {Total} generated)",
                        surplusQuestions.Count, generatedQuestions.Count);
                }
            }

            var totalQuestions = session.QuestionsByTour.Values.Sum(q => q.Count);
            _logger.LogInformation("Tour 1 prepared successfully for chat {ChatId}. Total: {Total} questions ({Reused} reused, {Generated} generated)",
                chatId, totalQuestions, reusedCount, generatedQuestions.Count);

            session.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(session, cancellationToken);

            var readyText = Localization.GetString(session.Language, "Bot.PoolReady");
            await Client.SendTextMessageAsync(chatId, readyText, cancellationToken: cancellationToken);

            // Start background preparation for remaining tours (2-8)
            if (session.Tours > 1)
            {
                _logger.LogInformation("Starting background preparation for tours 2-{MaxTour} for chat {ChatId}",
                    session.Tours, chatId);
                _ = PrepareRemainingToursInBackgroundAsync(session.Id, chatId, session.Topics, session.Tours,
                    session.RoundsPerTour, session.Players, session.Language, session.QuestionSourceMode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare question pool for chat {ChatId}", chatId);
            var failureText = string.Format(
                Localization.GetString(session.Language, "Bot.PoolFailure"),
                ex.Message);
            await Client.SendTextMessageAsync(chatId, failureText, cancellationToken: cancellationToken);
        }
    }

    private async Task PrepareRemainingToursInBackgroundAsync(
        Guid sessionId,
        long chatId,
        IReadOnlyList<string> topics,
        int totalTours,
        int roundsPerTour,
        IReadOnlyList<Player> players,
        GameLanguage language,
        QuestionSourceMode questionSourceMode)
    {
        try
        {
            _logger.LogInformation("Background: Preparing tours 2-{TotalTours} for session {SessionId}",
                totalTours, sessionId);

            var requiredPerTour = Math.Max(1, players.Count) * roundsPerTour;

            // Get archived questions for context
            var archivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(CancellationToken.None);

            for (var tourIndex = 1; tourIndex < totalTours; tourIndex++)
            {
                // Check if session still exists and game hasn't been cancelled
                var session = await Repository.LoadAsync(chatId, CancellationToken.None);
                if (session == null || session.Id != sessionId ||
                    session.Status == GameStatus.Cancelled || session.Status == GameStatus.Completed)
                {
                    _logger.LogInformation("Background: Stopping preparation - session no longer active");
                    return;
                }

                // Check if this tour already has questions
                if (session.QuestionsByTour.ContainsKey(tourIndex + 1) &&
                    session.QuestionsByTour[tourIndex + 1].Count > 0)
                {
                    _logger.LogDebug("Background: Tour {Tour} already prepared, skipping", tourIndex + 1);
                    continue;
                }

                var topic = topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
                _logger.LogInformation("Background: Preparing tour {Tour}/{Total} - topic {Topic}",
                    tourIndex + 1, totalTours, topic);

                var questionsFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, CancellationToken.None);

                List<Question> tourQuestions;
                if (questionsFromPool.Count >= requiredPerTour)
                {
                    // Enough from pool
                    tourQuestions = questionsFromPool.Take(requiredPerTour)
                        .Select(q => q with { Topic = topic })
                        .ToList();
                    _logger.LogDebug("Background: Reusing {Count} questions from pool for tour {Tour}",
                        requiredPerTour, tourIndex + 1);
                }
                else
                {
                    // Need to generate
                    _logger.LogInformation("Background: Generating {Needed} questions for tour {Tour}",
                        requiredPerTour - questionsFromPool.Count, tourIndex + 1);

                    var provider = _factory.Resolve(questionSourceMode);

                    IReadOnlyDictionary<int, List<Question>> generated;
                    if (provider is AiQuestionProvider aiProvider)
                    {
                        generated = await aiProvider.PrepareQuestionPoolAsync(
                            new[] { topic },
                            1,
                            roundsPerTour,
                            players,
                            language,
                            archivedQuestions,
                            CancellationToken.None);
                    }
                    else
                    {
                        generated = await provider.PrepareQuestionPoolAsync(
                            new[] { topic },
                            1,
                            roundsPerTour,
                            players,
                            language,
                            CancellationToken.None);
                    }

                    var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                    _logger.LogInformation("Background: Generated {Count} questions for tour {Tour}",
                        generatedList.Count, tourIndex + 1);

                    var combined = new List<Question>(questionsFromPool);
                    combined.AddRange(generatedList);
                    tourQuestions = combined.Take(requiredPerTour).ToList();

                    // Store surplus questions
                    var usedTexts = new HashSet<string>(
                        tourQuestions.Select(q => q.Text.Trim().ToLowerInvariant()),
                        StringComparer.OrdinalIgnoreCase);

                    var surplus = generatedList
                        .Where(q => !usedTexts.Contains(q.Text.Trim().ToLowerInvariant()))
                        .ToList();

                    if (surplus.Count > 0)
                    {
                        await _poolRepository.AddToUnusedPoolAsync(surplus, CancellationToken.None);
                        _logger.LogDebug("Background: Stored {Count} surplus questions", surplus.Count);
                    }
                }

                // Reload session and add questions
                session = await Repository.LoadAsync(chatId, CancellationToken.None);
                if (session != null && session.Id == sessionId)
                {
                    session.QuestionsByTour[tourIndex + 1] = new Queue<Question>(tourQuestions);
                    await Repository.SaveAsync(session, CancellationToken.None);
                    _logger.LogInformation("Background: Tour {Tour} prepared and saved ({Count} questions)",
                        tourIndex + 1, tourQuestions.Count);
                }
            }

            _logger.LogInformation("Background: Completed preparation for all tours for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background: Failed to prepare remaining tours for session {SessionId}", sessionId);
        }
    }
}

