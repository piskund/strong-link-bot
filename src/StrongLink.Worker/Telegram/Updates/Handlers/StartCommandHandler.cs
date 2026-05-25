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

        // IDEMPOTENCE: Check if a scheduled game is already initialized and waiting
        if (existingSession != null &&
            (existingSession.Status == GameStatus.AwaitingPlayers || existingSession.Status == GameStatus.ReadyToStart) &&
            existingSession.Metadata.TryGetValue("IsScheduledGame", out var isScheduledObj) &&
            TryGetBoolFromMetadata(isScheduledObj, out var isScheduled) && isScheduled &&
            existingSession.Metadata.TryGetValue("ScheduledAutoStartTime", out var autoStartObj) &&
            TryGetStringFromMetadata(autoStartObj, out var autoStartStr) &&
            DateTimeOffset.TryParse(autoStartStr, out var autoStartTime))
        {
            var timeRemaining = autoStartTime - DateTimeOffset.UtcNow;
            if (timeRemaining.TotalSeconds > 0)
            {
                var minutes = (int)Math.Ceiling(timeRemaining.TotalMinutes);
                var text = existingSession.Language == GameLanguage.Russian
                    ? $"⏳ Игра уже запланирована и автоматически начнется через {minutes} минут(ы).\n\n" +
                      $"Используйте /join чтобы присоединиться, или /begin чтобы начать сейчас."
                    : $"⏳ Game is already scheduled and will auto-start in {minutes} minute(s).\n\n" +
                      $"Use /join to participate, or /begin to start now.";

                _logger.LogInformation("Ignoring /start for chat {ChatId} - game already scheduled to start at {Time} (in {Minutes}m)",
                    chatId, autoStartTime, minutes);
                await Client.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
                return;
            }
            else
            {
                // Auto-start time has passed, let the command proceed to start the game
                _logger.LogInformation("Scheduled game auto-start time has passed for chat {ChatId}. Processing /start command normally.", chatId);
            }
        }

        // Use smart topic selection to prioritize topics with unused questions
        var selectedTopics = await TopicSelector.SelectOptimalTopicsAsync(
            _poolRepository,
            _gameOptions.Topics,
            _gameOptions.Tours,
            _logger,
            cancellationToken);

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
                DifficultyLevel = _gameOptions.DifficultyLevel,
                Topics = selectedTopics,
                Tours = _gameOptions.Tours,
                RoundsPerTour = _gameOptions.RoundsPerTour,
                AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                EliminateLowest = _gameOptions.EliminateLowest,
                MatureContent = _gameOptions.MatureContentEnabled,
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
                DifficultyLevel = _gameOptions.DifficultyLevel,
                Topics = selectedTopics,
                Tours = _gameOptions.Tours,
                RoundsPerTour = _gameOptions.RoundsPerTour,
                AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                EliminateLowest = _gameOptions.EliminateLowest,
                MatureContent = _gameOptions.MatureContentEnabled,
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

        var welcome = string.Format(
            Localization.GetString(session.Language, "Bot.Welcome"),
            VersionInfo.Version);
        await Client.SendTextMessageAsync(chatId, welcome, cancellationToken: cancellationToken);

        // Prepare question pool in background so bot stays responsive during generation
        _ = Task.Run(() => PrepareQuestionPoolAsync(session, CancellationToken.None), CancellationToken.None);
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
            // Assume at least 3 players — players join after /start so count is 0 here.
            // 3 players × 10 rounds = 30 questions, which is also one generation chunk.
            const int AssumedMinPlayers = 3;
            var requiredPerTour = Math.Max(AssumedMinPlayers, session.Players.Count) * session.RoundsPerTour;
            var MinQuestionsToStart = requiredPerTour;

            var poolStats = await _poolRepository.GetPoolStatsAsync(cancellationToken);
            _logger.LogInformation("Current pool stats: {Unused} unused, {Archived} archived",
                poolStats.Unused, poolStats.Archived);

            var topic = session.Topics.ElementAtOrDefault(0) ?? "Topic 1";
            var archivedForContext = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(topic, cancellationToken);
            var questionsFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, cancellationToken);

            var tour1Questions = questionsFromPool
                .Take(requiredPerTour)
                .Select(q => q with { Topic = topic })
                .ToList();

            // If pool doesn't have enough to reach the minimum, generate one chunk synchronously now.
            if (tour1Questions.Count < MinQuestionsToStart)
            {
                var needed = MinQuestionsToStart - tour1Questions.Count;
                _logger.LogInformation("Pool has {Count} questions for tour 1, generating {Needed} more (min {Min}) before marking ready",
                    tour1Questions.Count, needed, MinQuestionsToStart);

                var generatingMessage = session.Language == GameLanguage.Russian
                    ? $"🤖 Генерирую вопросы для тура 1: \"{topic}\"..."
                    : $"🤖 Generating questions for tour 1: \"{topic}\"...";
                await Client.SendTextMessageAsync(chatId, generatingMessage, cancellationToken: cancellationToken);

                var provider = _factory.Resolve(session.QuestionSourceMode);
                if (provider is AiQuestionProvider aiProvider)
                {
                    var generated = await aiProvider.PrepareQuestionPoolAsync(
                        new[] { topic }, 1, needed, session.Players, session.Language,
                        session.MatureContent, archivedForContext.TakeLast(100).ToList(),
                        session.DifficultyLevel, cancellationToken);
                    tour1Questions.AddRange(generated.Values.FirstOrDefault() ?? new List<Question>());
                }
                else
                {
                    var generated = await provider.PrepareQuestionPoolAsync(
                        new[] { topic }, 1, needed, session.Players, session.Language,
                        session.MatureContent, cancellationToken);
                    tour1Questions.AddRange(generated.Values.FirstOrDefault() ?? new List<Question>());
                }

                _logger.LogInformation("Initial generation complete: {Count} questions for tour 1", tour1Questions.Count);
            }

            // Reload before writing — game may have started while we were generating.
            var liveSession = await Repository.LoadAsync(chatId, cancellationToken);
            if (liveSession == null || liveSession.Id != session.Id)
            {
                _logger.LogInformation("Session replaced for chat {ChatId} during pool prep — discarding generated questions", chatId);
                if (tour1Questions.Count > 0)
                    await _poolRepository.AddToUnusedPoolAsync(tour1Questions, cancellationToken);
                return;
            }
            if (liveSession.Status == GameStatus.InProgress || liveSession.Status == GameStatus.SuddenDeath)
            {
                _logger.LogInformation("Game already started for chat {ChatId} during pool prep — depositing {Count} questions to pool", chatId, tour1Questions.Count);
                if (tour1Questions.Count > 0)
                    await _poolRepository.AddToUnusedPoolAsync(tour1Questions, cancellationToken);
                return;
            }

            liveSession.QuestionsByTour[1] = new Queue<Question>(tour1Questions);
            liveSession.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(liveSession, cancellationToken);
            session = liveSession;

            var readyText = Localization.GetString(session.Language, "Bot.PoolReady");
            await Client.SendTextMessageAsync(chatId, readyText, cancellationToken: cancellationToken);

            _logger.LogInformation("Session ready for chat {ChatId} with {Count} questions in tour 1. Filling remainder in background.",
                chatId, tour1Questions.Count);

            // Fill remaining questions for all tours in background.
            _ = PrepareAllToursInBackgroundAsync(session.Id, chatId, session.Topics, session.Tours,
                session.RoundsPerTour, session.Players, session.Language, session.QuestionSourceMode,
                session.MatureContent, session.DifficultyLevel, tour1Questions);
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

    private async Task PrepareAllToursInBackgroundAsync(
        Guid sessionId,
        long chatId,
        IReadOnlyList<string> topics,
        int totalTours,
        int roundsPerTour,
        IReadOnlyList<Player> players,
        GameLanguage language,
        QuestionSourceMode questionSourceMode,
        bool matureContent,
        DifficultyLevel difficultyLevel,
        IReadOnlyList<Question> alreadyInTour1)
    {
        try
        {
            _logger.LogInformation("Background: Preparing all {TotalTours} tours for session {SessionId}", totalTours, sessionId);

            var requiredPerTour = Math.Max(1, players.Count) * roundsPerTour;

            // Track questions already placed so we don't re-use them across tours
            var usedTexts = new HashSet<string>(
                alreadyInTour1.Select(q => q.Text.Trim().ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            for (var tourIndex = 0; tourIndex < totalTours; tourIndex++)
            {
                var session = await Repository.LoadAsync(chatId, CancellationToken.None);
                if (session == null || session.Id != sessionId ||
                    session.Status == GameStatus.Cancelled || session.Status == GameStatus.Completed)
                {
                    _logger.LogInformation("Background: Stopping - session gone or finished (status: {Status})", session?.Status);
                    return;
                }
                if (session.Status == GameStatus.InProgress || session.Status == GameStatus.SuddenDeath)
                {
                    _logger.LogInformation("Background: Game already started (status: {Status}) — remaining questions will go to pool", session.Status);
                    // Don't return — continue the loop so generated questions get deposited into the pool
                    // (the pre-save check below handles the actual routing to pool vs session)
                }

                var topic = topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";

                // Tour 1: top up whatever was already loaded from pool
                if (tourIndex == 0)
                {
                    var existing = session.QuestionsByTour.TryGetValue(1, out var q1) ? q1.Count : 0;
                    if (existing >= requiredPerTour)
                    {
                        _logger.LogDebug("Background: Tour 1 already fully loaded ({Count} questions), skipping generation", existing);
                        continue;
                    }
                }
                else if (session.QuestionsByTour.ContainsKey(tourIndex + 1) &&
                         session.QuestionsByTour[tourIndex + 1].Count >= requiredPerTour)
                {
                    _logger.LogDebug("Background: Tour {Tour} already prepared, skipping", tourIndex + 1);
                    continue;
                }

                _logger.LogInformation("Background: Preparing tour {Tour}/{Total} - topic '{Topic}'", tourIndex + 1, totalTours, topic);

                var questionsFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, CancellationToken.None);
                var freshFromPool = questionsFromPool
                    .Where(q => !usedTexts.Contains(q.Text.Trim().ToLowerInvariant()))
                    .Take(requiredPerTour)
                    .ToList();

                List<Question> tourQuestions = new(freshFromPool);
                foreach (var q in freshFromPool) usedTexts.Add(q.Text.Trim().ToLowerInvariant());

                if (tourQuestions.Count < requiredPerTour)
                {
                    var needed = requiredPerTour - tourQuestions.Count;
                    _logger.LogInformation("Background: Generating {Needed} questions for tour {Tour}", needed, tourIndex + 1);

                    var provider = _factory.Resolve(questionSourceMode);
                    var archivedForTopic = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(topic, CancellationToken.None);
                    var exclusions = archivedForTopic.TakeLast(100).ToList();

                    IReadOnlyDictionary<int, List<Question>> generated;
                    if (provider is AiQuestionProvider aiProvider)
                    {
                        generated = await aiProvider.PrepareQuestionPoolAsync(
                            new[] { topic }, 1, needed, players, language, matureContent,
                            exclusions, difficultyLevel, CancellationToken.None);
                    }
                    else
                    {
                        generated = await provider.PrepareQuestionPoolAsync(
                            new[] { topic }, 1, needed, players, language, matureContent, CancellationToken.None);
                    }

                    var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                    _logger.LogInformation("Background: Generated {Count} questions for tour {Tour}", generatedList.Count, tourIndex + 1);

                    var surplus = new List<Question>();
                    foreach (var q in generatedList)
                    {
                        var key = q.Text.Trim().ToLowerInvariant();
                        if (usedTexts.Contains(key)) continue;
                        usedTexts.Add(key);
                        if (tourQuestions.Count < requiredPerTour)
                            tourQuestions.Add(q);
                        else
                            surplus.Add(q);
                    }

                    if (surplus.Count > 0)
                    {
                        await _poolRepository.AddToUnusedPoolAsync(surplus, CancellationToken.None);
                        _logger.LogDebug("Background: Stored {Count} surplus questions in pool", surplus.Count);
                    }
                }

                // Reload to get latest session state before writing — never overwrite an active game.
                session = await Repository.LoadAsync(chatId, CancellationToken.None);
                if (session == null || session.Id != sessionId) return;

                if (session.Status == GameStatus.InProgress || session.Status == GameStatus.SuddenDeath)
                {
                    // Game started while we were generating — deposit into pool so EnsureQuestionsAvailableAsync picks them up.
                    if (tourQuestions.Count > 0)
                    {
                        await _poolRepository.AddToUnusedPoolAsync(tourQuestions, CancellationToken.None);
                        _logger.LogInformation("Background: Game already started — deposited {Count} tour {Tour} questions into pool", tourQuestions.Count, tourIndex + 1);
                    }
                    return;
                }

                if (!session.QuestionsByTour.TryGetValue(tourIndex + 1, out var liveQueue))
                {
                    liveQueue = new Queue<Question>();
                    session.QuestionsByTour[tourIndex + 1] = liveQueue;
                }

                var liveTexts = new HashSet<string>(liveQueue.Select(q => q.Text.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
                foreach (var q in tourQuestions)
                {
                    var key = q.Text.Trim().ToLowerInvariant();
                    if (!liveTexts.Contains(key))
                    {
                        liveQueue.Enqueue(q with { Topic = topic });
                        liveTexts.Add(key);
                    }
                }

                await Repository.SaveAsync(session, CancellationToken.None);
                _logger.LogInformation("Background: Tour {Tour} ready — {Count} questions in queue", tourIndex + 1, liveQueue.Count);
            }

            _logger.LogInformation("Background: All tours prepared for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background: Failed to prepare tours for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Safely extracts a boolean value from metadata object.
    /// Handles both direct bool values and JsonElement deserialization.
    /// </summary>
    private static bool TryGetBoolFromMetadata(object obj, out bool value)
    {
        value = false;

        if (obj == null)
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
        if (obj is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.False)
            {
                value = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Safely extracts a string value from metadata object.
    /// Handles both direct string values and JsonElement deserialization.
    /// </summary>
    private static bool TryGetStringFromMetadata(object obj, out string value)
    {
        value = string.Empty;

        if (obj == null)
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
        if (obj is System.Text.Json.JsonElement jsonElement &&
            jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = jsonElement.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }
}

