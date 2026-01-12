using System.Text.Json;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker.Services;

public sealed class ScheduledGameService : BackgroundService
{
    private readonly IGameSessionRepository _repository;
    private readonly IChatMessenger _messenger;
    private readonly IGameLifecycleService _lifecycleService;
    private readonly QuestionProviderFactory _questionProviderFactory;
    private readonly IQuestionPoolRepository _poolRepository;
    private readonly GameOptions _gameOptions;
    private readonly ILogger<ScheduledGameService> _logger;
    private DateTime _lastCheckDate = DateTime.MinValue;

    public ScheduledGameService(
        IGameSessionRepository repository,
        IChatMessenger messenger,
        IGameLifecycleService lifecycleService,
        QuestionProviderFactory questionProviderFactory,
        IQuestionPoolRepository poolRepository,
        IOptions<GameOptions> gameOptions,
        ILogger<ScheduledGameService> logger)
    {
        _repository = repository;
        _messenger = messenger;
        _lifecycleService = lifecycleService;
        _questionProviderFactory = questionProviderFactory;
        _poolRepository = poolRepository;
        _gameOptions = gameOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_gameOptions.EnableScheduledGames)
        {
            _logger.LogInformation("Scheduled games are disabled in configuration");
            return;
        }

        if (_gameOptions.ScheduledGameChatIds.Count == 0)
        {
            _logger.LogWarning("Scheduled games are enabled but no chat IDs are configured. " +
                "Add chat IDs to GameOptions.ScheduledGameChatIds in appsettings.json");
            return;
        }

        // Initialize last check date to prevent triggering past scheduled games on startup
        var now = DateTime.UtcNow;
        var currentDate = now.Date;
        var scheduledTime = currentDate.Add(_gameOptions.ScheduledGameTimeUtc);

        // If the scheduled time for today has already passed, mark it as processed
        // to prevent triggering it on startup
        if (now >= scheduledTime)
        {
            _lastCheckDate = currentDate;
            _logger.LogInformation("Scheduled game service started after today's scheduled time ({ScheduledTime}). " +
                "Next scheduled game will be tomorrow at {Time} UTC",
                scheduledTime, _gameOptions.ScheduledGameTimeUtc);
        }
        else
        {
            _logger.LogInformation("Scheduled game service started before today's scheduled time. " +
                "Games will start at {Time} UTC daily in {ChatCount} chat(s), with {WaitMinutes} minutes for players to join",
                _gameOptions.ScheduledGameTimeUtc, _gameOptions.ScheduledGameChatIds.Count, _gameOptions.ScheduledGameWaitMinutes);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndStartScheduledGamesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled game service");
            }

            // Check every minute
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task CheckAndStartScheduledGamesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var currentDate = now.Date;
        var scheduledTime = currentDate.Add(_gameOptions.ScheduledGameTimeUtc);

        // Check if we've already processed today's scheduled start
        if (_lastCheckDate == currentDate)
        {
            // Already processed today, check for auto-start timers
            await CheckAutoStartTimersAsync(cancellationToken);
            return;
        }

        // Check if it's time to trigger the scheduled start
        if (now >= scheduledTime && _lastCheckDate < currentDate)
        {
            _logger.LogInformation("Scheduled game time reached. Triggering scheduled game initialization");
            _lastCheckDate = currentDate;

            // Initialize scheduled games for configured chats
            foreach (var chatId in _gameOptions.ScheduledGameChatIds)
            {
                try
                {
                    await InitializeScheduledGameAsync(chatId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize scheduled game for chat {ChatId}", chatId);
                }
            }
        }

        // Check for games that need to auto-start
        await CheckAutoStartTimersAsync(cancellationToken);
    }

    private async Task InitializeScheduledGameAsync(long chatId, CancellationToken cancellationToken)
    {
        var session = await _repository.LoadAsync(chatId, cancellationToken);

        // Skip if there's an active game in progress
        if (session != null && (session.Status == GameStatus.InProgress ||
            session.Status == GameStatus.Paused ||
            session.Status == GameStatus.SuddenDeath))
        {
            _logger.LogInformation("Skipping scheduled game initialization for chat {ChatId} - game already in progress (status: {Status})",
                chatId, session.Status);
            return;
        }

        // Initialize scheduled game (will override any AwaitingPlayers session)
        _logger.LogInformation("Initializing scheduled game for chat {ChatId}", chatId);

        // Always create a fresh session for scheduled games
        // Use smart topic selection to prioritize topics with unused questions
        var selectedTopics = await TopicSelector.SelectOptimalTopicsAsync(
            _poolRepository,
            _gameOptions.Topics,
            _gameOptions.Tours,
            _logger,
            cancellationToken);

        session = new GameSession
        {
            ChatId = chatId,
            Status = GameStatus.AwaitingPlayers,
            Language = GameLanguage.Russian, // Default for scheduled games
            QuestionSourceMode = QuestionSourceMode.AI, // Default for scheduled games
            DifficultyLevel = _gameOptions.DifficultyLevel,
            Tours = _gameOptions.Tours,
            RoundsPerTour = _gameOptions.RoundsPerTour,
            AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
            EliminateLowest = _gameOptions.EliminateLowest,
            MatureContent = _gameOptions.MatureContentEnabled,
            Topics = selectedTopics
        };

        // Mark as scheduled game and set auto-start time
        var autoStartTime = DateTimeOffset.UtcNow.AddMinutes(_gameOptions.ScheduledGameWaitMinutes);
        session.Metadata["IsScheduledGame"] = true;
        session.Metadata["ScheduledAutoStartTime"] = autoStartTime.ToString("o");

        _logger.LogInformation("Scheduled game initialized for chat {ChatId}. Auto-start time: {AutoStartTime} (in {Minutes} minutes)",
            chatId, autoStartTime, _gameOptions.ScheduledGameWaitMinutes);

        await _repository.SaveAsync(session, cancellationToken);

        // Send notification to chat
        var message = session.Language == GameLanguage.Russian
            ? $"🎮 Запланированная игра начинается!\n\n" +
              $"Используйте /join чтобы присоединиться.\n" +
              $"⏳ Подготавливаю вопросы... Игра автоматически начнется через {_gameOptions.ScheduledGameWaitMinutes} минут, если присоединится хотя бы 1 игрок."
            : $"🎮 Scheduled game is starting!\n\n" +
              $"Use /join to participate.\n" +
              $"⏳ Preparing questions... The game will automatically begin in {_gameOptions.ScheduledGameWaitMinutes} minutes if at least 1 player joins.";

        await _messenger.SendAsync(chatId, message, cancellationToken);

        // Prepare questions immediately in the background during the wait period
        // This allows the game to start instantly when the timer expires
        _logger.LogInformation("Starting background question preparation for scheduled game in chat {ChatId}", chatId);

        try
        {
            await PrepareQuestionsForGameAsync(session, cancellationToken);
            _logger.LogInformation("Questions prepared successfully for scheduled game in chat {ChatId}", chatId);

            // Notify chat that questions are ready
            var readyMessage = session.Language == GameLanguage.Russian
                ? "✅ Вопросы готовы! Ожидаем игроков..."
                : "✅ Questions ready! Waiting for players...";

            await _messenger.SendAsync(chatId, readyMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare questions for scheduled game in chat {ChatId}. Will retry on auto-start.", chatId);

            // Mark that question preparation failed, so we can retry on auto-start
            session.Metadata["QuestionPreparationFailed"] = true;
            await _repository.SaveAsync(session, cancellationToken);

            var errorMessage = session.Language == GameLanguage.Russian
                ? "⚠️ Не удалось подготовить вопросы сейчас. Повторю попытку при старте игры."
                : "⚠️ Failed to prepare questions now. Will retry when the game starts.";

            await _messenger.SendAsync(chatId, errorMessage, cancellationToken);
        }
    }

    private async Task CheckAutoStartTimersAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Checking auto-start timers for {Count} scheduled chat(s)", _gameOptions.ScheduledGameChatIds.Count);

        foreach (var chatId in _gameOptions.ScheduledGameChatIds)
        {
            try
            {
                var session = await _repository.LoadAsync(chatId, cancellationToken);
                if (session == null)
                {
                    _logger.LogDebug("No session found for chat {ChatId}", chatId);
                    continue;
                }

                _logger.LogDebug("Chat {ChatId} session status: {Status}, has IsScheduledGame: {HasFlag}",
                    chatId, session.Status, session.Metadata.ContainsKey("IsScheduledGame"));

                // Check if this is a scheduled game waiting to auto-start
                // Note: Status can be AwaitingPlayers (questions not yet prepared) or ReadyToStart (questions prepared)
                if ((session.Status == GameStatus.AwaitingPlayers || session.Status == GameStatus.ReadyToStart) &&
                    TryGetBoolFromMetadata(session.Metadata, "IsScheduledGame", out var isScheduled) &&
                    isScheduled &&
                    TryGetStringFromMetadata(session.Metadata, "ScheduledAutoStartTime", out var autoStartStr) &&
                    DateTimeOffset.TryParse(autoStartStr, out var autoStartTime))
                {
                    if (DateTimeOffset.UtcNow >= autoStartTime)
                    {
                        _logger.LogInformation("Auto-start time reached for scheduled game in chat {ChatId}", chatId);
                        await AutoStartScheduledGameAsync(session, cancellationToken);
                    }
                    else
                    {
                        _logger.LogDebug("Scheduled game in chat {ChatId} waiting for auto-start at {AutoStartTime} (current: {Now})",
                            chatId, autoStartTime, DateTimeOffset.UtcNow);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking auto-start timer for chat {ChatId}", chatId);
            }
        }
    }

    private async Task AutoStartScheduledGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        // Mark that this was a scheduled game so StartGameAsync knows not to show regular start message
        session.Metadata["WasScheduledGame"] = true;

        // Clear scheduled game metadata
        session.Metadata.Remove("IsScheduledGame");
        session.Metadata.Remove("ScheduledAutoStartTime");

        if (session.Players.Count == 0)
        {
            // No players joined, cancel the game
            _logger.LogInformation("No players joined scheduled game in chat {ChatId}. Cancelling.", session.ChatId);

            // Return prepared questions to the unused pool for future games
            if (session.QuestionsByTour.Count > 0)
            {
                var allPreparedQuestions = session.QuestionsByTour.Values
                    .SelectMany(q => q)
                    .ToList();

                if (allPreparedQuestions.Count > 0)
                {
                    try
                    {
                        await _poolRepository.AddToUnusedPoolAsync(allPreparedQuestions, cancellationToken);
                        _logger.LogInformation("Returned {QuestionCount} prepared questions to unused pool for chat {ChatId}",
                            allPreparedQuestions.Count, session.ChatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to return questions to pool for chat {ChatId}. Questions will be lost.",
                            session.ChatId);
                    }
                }
            }

            session.Status = GameStatus.Cancelled;
            await _repository.SaveAsync(session, cancellationToken);

            var message = session.Language == GameLanguage.Russian
                ? "⏰ Время вышло! Никто не присоединился к запланированной игре. Игра отменена."
                : "⏰ Time's up! No one joined the scheduled game. Game cancelled.";

            await _messenger.SendAsync(session.ChatId, message, cancellationToken);
        }
        else
        {
            // Start the game
            _logger.LogInformation("Auto-starting scheduled game in chat {ChatId} with {PlayerCount} player(s)",
                session.ChatId, session.Players.Count);

            // Check if questions were already prepared during the wait period
            var questionsPrepared = session.QuestionsByTour.Count > 0;
            var preparationFailed = TryGetBoolFromMetadata(session.Metadata, "QuestionPreparationFailed", out var failed) && failed;

            if (!questionsPrepared || preparationFailed)
            {
                // Questions not prepared yet or failed earlier - prepare now
                _logger.LogInformation("Questions not ready for chat {ChatId}. Preparing now...", session.ChatId);

                var preparingMessage = session.Language == GameLanguage.Russian
                    ? "⏰ Время вышло! Это запланированная игра - автоматический старт.\n⏳ Подготавливаю вопросы..."
                    : "⏰ Time's up! This is a scheduled game - auto-starting now.\n⏳ Preparing questions...";

                await _messenger.SendAsync(session.ChatId, preparingMessage, cancellationToken);

                try
                {
                    await PrepareQuestionsForGameAsync(session, cancellationToken);
                    session.Metadata.Remove("QuestionPreparationFailed");
                    _logger.LogInformation("Questions prepared successfully for scheduled game in chat {ChatId}", session.ChatId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to prepare questions for scheduled game in chat {ChatId}", session.ChatId);

                    var errorMessage = session.Language == GameLanguage.Russian
                        ? "❌ Не удалось подготовить вопросы для игры. Игра отменена."
                        : "❌ Failed to prepare questions for the game. Game cancelled.";

                    await _messenger.SendAsync(session.ChatId, errorMessage, cancellationToken);

                    session.Status = GameStatus.Cancelled;
                    await _repository.SaveAsync(session, cancellationToken);
                    return;
                }
            }
            else
            {
                // Questions already prepared - start immediately
                _logger.LogInformation("Questions already prepared for chat {ChatId}. Starting game immediately.", session.ChatId);

                var startMessage = session.Language == GameLanguage.Russian
                    ? "⏰ Время вышло! Это запланированная игра - автоматический старт."
                    : "⏰ Time's up! This is a scheduled game - auto-starting now.";

                await _messenger.SendAsync(session.ChatId, startMessage, cancellationToken);
            }

            // Start the game (which will send the regular game start message)
            await _lifecycleService.StartGameAsync(session, cancellationToken);
        }
    }

    /// <summary>
    /// Safely extracts a boolean value from metadata dictionary.
    /// Handles both direct bool values and JsonElement deserialization.
    /// </summary>
    private static bool TryGetBoolFromMetadata(Dictionary<string, object> metadata, string key, out bool value)
    {
        value = false;

        if (!metadata.TryGetValue(key, out var obj) || obj == null)
        {
            return false;
        }

        // Handle direct bool
        if (obj is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        // Handle JsonElement (from deserialization)
        if (obj is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (obj is JsonElement jsonElementFalse && jsonElementFalse.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Safely extracts a string value from metadata dictionary.
    /// Handles both direct string values and JsonElement deserialization.
    /// </summary>
    private static bool TryGetStringFromMetadata(Dictionary<string, object> metadata, string key, out string value)
    {
        value = string.Empty;

        if (!metadata.TryGetValue(key, out var obj) || obj == null)
        {
            return false;
        }

        // Handle direct string
        if (obj is string strValue)
        {
            value = strValue;
            return true;
        }

        // Handle JsonElement (from deserialization)
        if (obj is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
        {
            value = jsonElement.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prepares questions for a scheduled game session.
    /// Generates only the first tour's questions to start quickly.
    /// Subsequent tours will be generated during pause between tours.
    /// </summary>
    private async Task PrepareQuestionsForGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        // For scheduled games, prepare questions for only the first tour
        // This minimizes wait time before game starts
        // Next tours will be generated during the pause between tours
        var toursToPrep = 1;

        _logger.LogInformation("Preparing questions for first tour for scheduled game in chat {ChatId}",
            session.ChatId);

        // Notify chat that question preparation is starting
        var startMessage = session.Language == GameLanguage.Russian
            ? "🔄 Начинаю подготовку вопросов для первого тура..."
            : "🔄 Starting question preparation for the first tour...";

        await _messenger.SendAsync(session.ChatId, startMessage, cancellationToken);

        // Use at least 3 players when calculating required questions
        var effectivePlayerCount = Math.Max(3, session.Players.Count);
        var requiredPerTour = effectivePlayerCount * session.RoundsPerTour;

        _logger.LogDebug("Question calculation: {EffectiveCount} players * {Rounds} rounds = {Required} per tour",
            effectivePlayerCount, session.RoundsPerTour, requiredPerTour);

        // Get archived questions to avoid repetition
        var archivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(cancellationToken);
        _logger.LogDebug("Retrieved {Count} archived questions for context", archivedQuestions.Count);

        session.QuestionsByTour.Clear();

        for (var tourIndex = 0; tourIndex < toursToPrep; tourIndex++)
        {
            var topic = session.Topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";

            // Try to get questions from unused pool first
            var questionsFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, cancellationToken);
            _logger.LogDebug("Found {Count} unused questions in pool for tour {Tour} topic '{Topic}'",
                questionsFromPool.Count, tourIndex + 1, topic);

            if (questionsFromPool.Count >= requiredPerTour)
            {
                // Enough questions in pool
                session.QuestionsByTour[tourIndex + 1] = new Queue<Question>(
                    questionsFromPool.Take(requiredPerTour).Select(q => q with { Topic = topic })
                );
                _logger.LogDebug("Using {Count} pooled questions for tour {Tour}", requiredPerTour, tourIndex + 1);
            }
            else
            {
                // Need to generate questions
                var provider = _questionProviderFactory.Resolve(session.QuestionSourceMode);

                // Notify chat that generation is starting for this tour
                var generatingMessage = session.Language == GameLanguage.Russian
                    ? $"🤖 Генерирую вопросы для тура {tourIndex + 1}: \"{topic}\"..."
                    : $"🤖 Generating questions for tour {tourIndex + 1}: \"{topic}\"...";

                await _messenger.SendAsync(session.ChatId, generatingMessage, cancellationToken);

                IReadOnlyDictionary<int, List<Question>> generated;
                if (provider is AiQuestionProvider aiProvider)
                {
                    generated = await aiProvider.PrepareQuestionPoolAsync(
                        new[] { topic },
                        1,
                        requiredPerTour,
                        session.Players,
                        session.Language,
                        session.MatureContent,
                        archivedQuestions,
                        cancellationToken);
                }
                else
                {
                    generated = await provider.PrepareQuestionPoolAsync(
                        new[] { topic },
                        1,
                        requiredPerTour,
                        session.Players,
                        session.Language,
                        session.MatureContent,
                        cancellationToken);
                }

                var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                _logger.LogDebug("Generated {Count} questions for tour {Tour} topic '{Topic}'",
                    generatedList.Count, tourIndex + 1, topic);

                // Combine pool + generated questions
                var combined = new List<Question>(questionsFromPool);
                combined.AddRange(generatedList);

                session.QuestionsByTour[tourIndex + 1] = new Queue<Question>(
                    combined.Take(requiredPerTour).Select(q => q with { Topic = topic })
                );

                // Store surplus generated questions in unused pool
                if (generatedList.Count > requiredPerTour - questionsFromPool.Count)
                {
                    var surplus = generatedList.Skip(requiredPerTour - questionsFromPool.Count).ToList();
                    if (surplus.Count > 0)
                    {
                        await _poolRepository.AddToUnusedPoolAsync(surplus, cancellationToken);
                        _logger.LogDebug("Added {Count} surplus questions to unused pool", surplus.Count);
                    }
                }
            }

            // Send progress update after each tour
            var progressMessage = session.Language == GameLanguage.Russian
                ? $"✅ Тур {tourIndex + 1}/{toursToPrep} готов (тема: \"{topic}\")"
                : $"✅ Tour {tourIndex + 1}/{toursToPrep} ready (topic: \"{topic}\")";

            await _messenger.SendAsync(session.ChatId, progressMessage, cancellationToken);
        }

        var totalQuestions = session.QuestionsByTour.Values.Sum(q => q.Count);
        _logger.LogInformation("Prepared {Total} questions across {Tours} tour(s) for scheduled game",
            totalQuestions, toursToPrep);

        session.Status = GameStatus.ReadyToStart;
        await _repository.SaveAsync(session, cancellationToken);
    }
}
