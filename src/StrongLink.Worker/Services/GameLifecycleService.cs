using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker.Services;

public sealed class GameLifecycleService : IGameLifecycleService
{
    private readonly IChatMessenger _messenger;
    private readonly IGameSessionRepository _repository;
    private readonly ILocalizationService _localization;
    private readonly IQuestionPoolRepository _poolRepository;
    private readonly IGameResultRepository _resultRepository;
    private readonly IAnswerValidator _answerValidator;
    private readonly QuestionProviderFactory _questionProviderFactory;
    private readonly ISuddenDeathService _suddenDeathService;
    private readonly IGameModeScoreHandler _regularScoreHandler;
    private readonly IGameModeScoreHandler _suddenDeathScoreHandler;
    private readonly GameOptions _gameOptions;
    private readonly ILogger<GameLifecycleService> _logger;

    // Track active answer timers: (chatId, questionAskedAt) -> CancellationTokenSource
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long, DateTimeOffset), CancellationTokenSource> _answerTimers = new();

    public GameLifecycleService(
        IChatMessenger messenger,
        IGameSessionRepository repository,
        ILocalizationService localization,
        IQuestionPoolRepository poolRepository,
        IGameResultRepository resultRepository,
        IAnswerValidator answerValidator,
        QuestionProviderFactory questionProviderFactory,
        ISuddenDeathService suddenDeathService,
        RegularModeScoreHandler regularScoreHandler,
        SuddenDeathModeScoreHandler suddenDeathScoreHandler,
        IOptions<GameOptions> gameOptions,
        ILogger<GameLifecycleService> logger)
    {
        _messenger = messenger;
        _repository = repository;
        _localization = localization;
        _poolRepository = poolRepository;
        _resultRepository = resultRepository;
        _answerValidator = answerValidator;
        _questionProviderFactory = questionProviderFactory;
        _suddenDeathService = suddenDeathService;
        _regularScoreHandler = regularScoreHandler;
        _suddenDeathScoreHandler = suddenDeathScoreHandler;
        _gameOptions = gameOptions.Value;
        _logger = logger;
    }

    public async Task StartGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartGameAsync called for chat {ChatId}. Players: {PlayerCount}, Status: {Status}",
            session.ChatId, session.Players.Count, session.Status);

        if (session.Players.Count < 1)
        {
            _logger.LogWarning("Not enough players to start game. Players: {PlayerCount}", session.Players.Count);
            var text = _localization.GetString(session.Language, "Game.NotEnoughPlayers");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        if (session.QuestionsByTour.Count == 0)
        {
            _logger.LogWarning("No question pool available for chat {ChatId}", session.ChatId);
            var text = _localization.GetString(session.Language, "Game.NoQuestionPool");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        _logger.LogInformation("Starting game for chat {ChatId} with {PlayerCount} players, {TourCount} tours",
            session.ChatId, session.Players.Count, session.Tours);

        // Always send regular game start message
        var gameStartMessage = session.Language == GameLanguage.Russian
            ? $"🎮 Игра начинается с {session.Players.Count} игроком(ами)!"
            : $"🎮 Starting game with {session.Players.Count} player(s)!";
        await _messenger.SendAsync(session.ChatId, gameStartMessage, cancellationToken);

        // Clean up scheduled game flag if present
        if (session.Metadata.ContainsKey("WasScheduledGame"))
        {
            session.Metadata.Remove("WasScheduledGame");
        }

        session.Status = GameStatus.InProgress;
        session.StartedAt = DateTimeOffset.UtcNow;
        session.CurrentTour = 1;
        session.CurrentRound = 0;
        session.CurrentQuestion = null;
        session.TurnQueue.Clear();

        // Initialize tracking for asked questions
        if (!session.Metadata.ContainsKey("AskedQuestions"))
        {
            session.Metadata["AskedQuestions"] = new List<Question>();
        }

        foreach (var player in session.ActivePlayers)
        {
            session.TurnQueue.Enqueue(player.Id);
            _logger.LogDebug("Added player {PlayerName} (ID: {PlayerId}) to turn queue", player.DisplayName, player.Id);
        }

        await _repository.SaveAsync(session, cancellationToken);

        _logger.LogInformation("Game started successfully for chat {ChatId}. Advancing to first round.", session.ChatId);
        await AdvanceRoundAsync(session, cancellationToken);
    }

    public async Task AdvanceRoundAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogDebug("AdvanceRoundAsync: Status: {Status}, Tour {Tour}, Round {Round}, TurnQueue: {QueueCount}",
            session.Status, session.CurrentTour, session.CurrentRound, session.TurnQueue.Count);

        // Check if we need to generate more questions
        await EnsureQuestionsAvailableAsync(session, cancellationToken);

        if (!session.QuestionsByTour.TryGetValue(session.CurrentTour, out var questions) || questions.Count == 0)
        {
            _logger.LogInformation("No questions remaining for tour {Tour}. Completing tour.", session.CurrentTour);

            // Safety check: if we've already tried to complete a tour due to lack of questions,
            // and we still have no questions, end the game to avoid infinite loop
            if (session.Metadata.TryGetValue("LastNoQuestionsTour", out var lastTourObj) &&
                lastTourObj is int lastTour && lastTour == session.CurrentTour - 1)
            {
                _logger.LogWarning("Detected consecutive tours with no questions. Ending game to prevent infinite loop. Tour: {Tour}",
                    session.CurrentTour);
                session.Metadata.Remove("LastNoQuestionsTour");
                await CompleteGameAsync(session, cancellationToken);
                return;
            }

            // Mark that we're completing a tour due to lack of questions
            session.Metadata["LastNoQuestionsTour"] = session.CurrentTour;
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        // Clear the flag if we have questions
        session.Metadata.Remove("LastNoQuestionsTour");

        if (session.TurnQueue.Count == 0)
        {
            // In sudden death mode, check if ties are resolved after each round
            if (session.Status == GameStatus.SuddenDeath)
            {
                // Get the sudden death starting round from metadata
                if (!session.Metadata.TryGetValue("SuddenDeathStartRound", out var startRoundObj) ||
                    startRoundObj is not int startRound)
                {
                    _logger.LogError("SuddenDeathStartRound not found in metadata! This should not happen.");
                    startRound = session.CurrentRound; // Fallback
                }

                var suddenDeathRoundsPlayed = session.CurrentRound - startRound;

                // Check if we've reached the sudden death round limit
                if (suddenDeathRoundsPlayed >= session.RoundsPerTour)
                {
                    _logger.LogWarning("Sudden death round limit reached ({Limit} rounds). Ties remain unresolved. Moving all survivors to next tour.",
                        session.RoundsPerTour);

                    var timeoutText = session.Language == GameLanguage.Russian
                        ? $"⏱️ Внезапная смерть: достигнут лимит раундов ({session.RoundsPerTour}). Все выжившие переходят в следующий тур!"
                        : $"⏱️ Sudden death: round limit reached ({session.RoundsPerTour}). All survivors advance to the next tour!";
                    await _messenger.SendAsync(session.ChatId, timeoutText, cancellationToken);

                    // Exit sudden death mode without eliminating anyone
                    _suddenDeathService.ExitSuddenDeath(session);

                    await _repository.SaveAsync(session, cancellationToken);

                    // Check if game should end
                    var remaining = session.ActivePlayers.ToList();
                    if (remaining.Count <= 1)
                    {
                        await CompleteGameAsync(session, cancellationToken);
                        return;
                    }

                    // All survivors move to the next tour (joining high performers who moved there previously)
                    session.Metadata["SkipSuddenDeathCheck"] = true;
                    _logger.LogInformation("Sudden death timeout. {Count} survivors moving to next tour without elimination.", remaining.Count);
                    await CompleteTourAsync(session, cancellationToken);
                    return;
                }

                var resolution = _suddenDeathService.CheckIfSuddenDeathResolved(session);

                if (resolution.IsResolved)
                {
                    _logger.LogInformation("Sudden death resolved after {Rounds} round(s). Eliminating {Count} player(s).",
                        suddenDeathRoundsPlayed + 1, resolution.ToEliminate.Count);

                    var resolvedText = _localization.GetString(session.Language, "Game.SuddenDeathResolved");
                    await _messenger.SendAsync(session.ChatId, resolvedText, cancellationToken);

                    // Eliminate players with the lowest score
                    foreach (var player in resolution.ToEliminate)
                    {
                        player.Status = PlayerStatus.Eliminated;
                        _logger.LogInformation("Player {PlayerName} eliminated after sudden death. SuddenDeathScore: {SuddenDeathScore}, MainScore: {Score}",
                            player.DisplayName, player.SuddenDeathScore, player.Score);
                        var elimText = string.Format(
                            _localization.GetString(session.Language, "Game.Eliminated"),
                            player.DisplayName);
                        await _messenger.SendAsync(session.ChatId, elimText, cancellationToken);
                    }

                    // Exit sudden death mode
                    _suddenDeathService.ExitSuddenDeath(session);

                    await _repository.SaveAsync(session, cancellationToken);

                    // Check if game should end
                    var remaining = session.ActivePlayers.ToList();
                    if (remaining.Count <= 1)
                    {
                        _logger.LogInformation("Only {Count} active player(s) remaining after sudden death elimination. Completing game.", remaining.Count);
                        await CompleteGameAsync(session, cancellationToken);
                        return;
                    }

                    // Safety check: Ensure we don't have ties in sudden death scores among remaining players
                    var remainingSuddenDeathScores = remaining.Select(p => p.SuddenDeathScore).Distinct().ToList();
                    if (remaining.Count > 1 && remainingSuddenDeathScores.Count == 1 && remainingSuddenDeathScores.First() > 0)
                    {
                        _logger.LogWarning("After sudden death elimination, {Count} players remain with tied sudden death scores ({Score}). This should not happen!",
                            remaining.Count, remainingSuddenDeathScores.First());
                    }

                    // Survivors move to the next tour
                    // Set a flag to indicate we just resolved sudden death, so CompleteTourAsync
                    // should skip sudden death checks and just move to next tour
                    session.Metadata["SkipSuddenDeathCheck"] = true;
                    _logger.LogInformation("Sudden death resolved after round. {Count} survivors moving to next tour.", remaining.Count);
                    await CompleteTourAsync(session, cancellationToken);
                    return;
                }
                else
                {
                    _logger.LogInformation("Ties still present in sudden death after {Rounds} round(s). Continuing.",
                        suddenDeathRoundsPlayed + 1);

                    // Queue only sudden death participants for next round
                    if (resolution.Survivors.Count == 0)
                    {
                        _logger.LogError("No survivors in sudden death - this should not happen. Ending game.");
                        await CompleteGameAsync(session, cancellationToken);
                        return;
                    }

                    foreach (var player in resolution.Survivors)
                    {
                        session.TurnQueue.Enqueue(player.Id);
                    }
                }

                session.CurrentRound += 1;
                _logger.LogInformation("Starting sudden death round {Round} (sudden death round {SDRound})",
                    session.CurrentRound + 1, suddenDeathRoundsPlayed + 2);
            }
            else
            {
                // Normal game mode
                _logger.LogDebug("Turn queue empty. Refilling with {ActivePlayerCount} active players.", session.ActivePlayers.Count());

                foreach (var activePlayer in session.ActivePlayers)
                {
                    session.TurnQueue.Enqueue(activePlayer.Id);
                }

                session.CurrentRound += 1;
                _logger.LogInformation("Starting round {Round}/{MaxRounds} for tour {Tour}",
                    session.CurrentRound + 1, session.RoundsPerTour, session.CurrentTour);

                // Announce tour topic at the start of the first round
                if (session.CurrentRound == 1)
                {
                    var currentTopic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
                    var tourStartText = string.Format(
                        _localization.GetString(session.Language, "Game.TourStart"),
                        session.CurrentTour,
                        currentTopic);
                    await _messenger.SendAsync(session.ChatId, tourStartText, cancellationToken);
                    _logger.LogInformation("Announced tour {Tour} topic: {Topic}", session.CurrentTour, currentTopic);
                }
            }
        }

        // Only check max rounds limit if NOT in sudden death mode
        if (session.Status != GameStatus.SuddenDeath && session.CurrentRound >= session.RoundsPerTour)
        {
            _logger.LogInformation("Reached max rounds ({MaxRounds}). Completing tour {Tour}.",
                session.RoundsPerTour, session.CurrentTour);
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        if (session.TurnQueue.Count == 0)
        {
            _logger.LogWarning("Turn queue still empty after refill. Completing tour.");
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        session.CurrentPlayerId = session.TurnQueue.Dequeue();
        session.CurrentQuestion = questions.Dequeue();
        session.CurrentQuestionAskedAt = DateTimeOffset.UtcNow;

        // Track asked questions for archiving later
        if (session.Metadata.TryGetValue("AskedQuestions", out var askedObj))
        {
            var askedQuestions = ExtractAskedQuestions(askedObj);
            askedQuestions.Add(session.CurrentQuestion);
            // Update the metadata with the modified list
            session.Metadata["AskedQuestions"] = askedQuestions;
        }

        await _repository.SaveAsync(session, cancellationToken);

        var currentPlayer = session.FindPlayer(session.CurrentPlayerId.Value);
        if (currentPlayer is null)
        {
            _logger.LogWarning("Current player {PlayerId} not found. Advancing to next player.", session.CurrentPlayerId.Value);
            await AdvanceRoundAsync(session, cancellationToken);
            return;
        }

        _logger.LogInformation("Asking question to player {PlayerName} (ID: {PlayerId}): {Question}",
            currentPlayer.DisplayName, currentPlayer.Id, session.CurrentQuestion.Text);

        string text;
        if (session.Status == GameStatus.SuddenDeath)
        {
            text = string.Format(
                _localization.GetString(session.Language, "Game.SuddenDeathRound"),
                currentPlayer.DisplayName,
                session.CurrentQuestion.Text,
                session.AnswerTimeoutSeconds);
        }
        else
        {
            var currentTopic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
            text = string.Format(
                _localization.GetString(session.Language, "Game.Round"),
                session.CurrentTour,
                currentTopic,
                session.CurrentRound + 1,
                session.RoundsPerTour,
                currentPlayer.DisplayName,
                session.CurrentQuestion.Text,
                session.AnswerTimeoutSeconds);
        }

        int messageId;
        if (!string.IsNullOrWhiteSpace(session.CurrentQuestion.ImageUrl))
        {
            try
            {
                // Try to send question with image
                messageId = await _messenger.SendPhotoAsync(session.ChatId, session.CurrentQuestion.ImageUrl, text, cancellationToken);
            }
            catch (Exception ex)
            {
                // If image fails, fall back to text-only
                _logger.LogWarning(ex, "Failed to send question with image {ImageUrl}, falling back to text-only",
                    session.CurrentQuestion.ImageUrl);
                messageId = await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            }
        }
        else
        {
            // Send regular text question
            messageId = await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        }
        session.CurrentQuestionMessageId = messageId;
        await _repository.SaveAsync(session, cancellationToken);

        // Start answer timeout timer
        StartAnswerTimer(session.ChatId, session.CurrentQuestionAskedAt.Value, session.AnswerTimeoutSeconds);
    }

    public async Task HandleAnswerAsync(GameSession session, long playerId, string answer, CancellationToken cancellationToken)
    {
        _logger.LogDebug("HandleAnswerAsync: Player {PlayerId} answered: {Answer}", playerId, answer);

        if (session.Status == GameStatus.Paused)
        {
            _logger.LogDebug("Game is paused. Ignoring answer from {PlayerId}", playerId);
            return;
        }

        if (session.CurrentQuestion is null || session.CurrentPlayerId is null)
        {
            _logger.LogWarning("No current question or player set. Ignoring answer from {PlayerId}", playerId);
            return;
        }

        if (playerId != session.CurrentPlayerId.Value)
        {
            _logger.LogDebug("Answer from wrong player {PlayerId}. Expected {ExpectedPlayerId}",
                playerId, session.CurrentPlayerId.Value);
            var text = _localization.GetString(session.Language, "Game.AnswerIgnored");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        var player = session.FindPlayer(playerId);
        if (player is null)
        {
            _logger.LogWarning("Player {PlayerId} not found in session", playerId);
            await AdvanceRoundAsync(session, cancellationToken);
            return;
        }

        // Cancel the answer timeout timer
        if (session.CurrentQuestionAskedAt.HasValue)
        {
            CancelAnswerTimer(session.ChatId, session.CurrentQuestionAskedAt.Value);
        }

        bool isCorrect;

        if (_gameOptions.UseAiAnswerValidation)
        {
            // Use AI-powered semantic validation
            isCorrect = await _answerValidator.ValidateAnswerAsync(
                answer,
                session.CurrentQuestion.Answer,
                session.CurrentQuestion.Text,
                session.Language,
                session.DifficultyLevel,
                cancellationToken);
        }
        else
        {
            // Use simple string comparison
            var normalizedAnswer = Normalize(answer);
            var normalizedCorrect = Normalize(session.CurrentQuestion.Answer);
            isCorrect = string.Equals(normalizedAnswer, normalizedCorrect, StringComparison.OrdinalIgnoreCase);
        }

        if (isCorrect)
        {
            // Update score using the appropriate handler for the current game mode
            var scoreHandler = session.Status == GameStatus.SuddenDeath
                ? _suddenDeathScoreHandler
                : _regularScoreHandler;
            scoreHandler.UpdateScore(player, isCorrect: true);
            player.CorrectAnswers += 1;
            var text = _localization.GetString(session.Language, "Game.Correct");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        }
        else
        {
            player.IncorrectAnswers += 1;
            _logger.LogInformation("Player {PlayerName} answered INCORRECTLY. Answer: '{Answer}', Correct: '{Correct}'",
                player.DisplayName, answer, session.CurrentQuestion.Answer);
            var text = string.Format(
                _localization.GetString(session.Language, "Game.Incorrect"),
                session.CurrentQuestion.Answer);
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        }

        session.CurrentQuestion = null;
        session.CurrentPlayerId = null;
        session.CurrentQuestionAskedAt = null;
        session.CurrentQuestionMessageId = null;
        await _repository.SaveAsync(session, cancellationToken);

        // Don't check for sudden death elimination immediately after each answer
        // Only check at the end of the round (in AdvanceRoundAsync when turn queue is empty)

        await AdvanceRoundAsync(session, cancellationToken);
    }

    public async Task StopGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping game for chat {ChatId}", session.ChatId);

        // Cancel any active answer timer to prevent the game from continuing
        if (session.CurrentQuestionAskedAt.HasValue)
        {
            CancelAnswerTimer(session.ChatId, session.CurrentQuestionAskedAt.Value);
            _logger.LogInformation("Cancelled active answer timer for chat {ChatId}", session.ChatId);
        }

        session.Status = GameStatus.Cancelled;
        session.CompletedAt = DateTimeOffset.UtcNow;

        // Archive used questions if any
        if (session.Metadata.TryGetValue("AskedQuestions", out var askedObj))
        {
            var askedQuestions = ExtractAskedQuestions(askedObj);
            if (askedQuestions.Count > 0)
            {
                try
                {
                    await _poolRepository.MoveToArchiveAsync(askedQuestions, cancellationToken);
                    _logger.LogInformation("Archived {Count} used questions from stopped game in chat {ChatId}",
                        askedQuestions.Count, session.ChatId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to archive used questions for stopped game in chat {ChatId}", session.ChatId);
                }
            }
        }

        // Create and archive game result
        try
        {
            var gameResult = CreateGameResult(session);
            await _resultRepository.ArchiveAsync(gameResult, cancellationToken);
            _logger.LogInformation("Archived result for stopped game {GameId} in chat {ChatId}", gameResult.GameId, session.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive game result for chat {ChatId}", session.ChatId);
        }

        // Save the stopped session (don't delete it - keep it for /standings)
        // Session will be cleared when /start is called for a new game
        await _repository.SaveAsync(session, cancellationToken);

        var text = _localization.GetString(session.Language, "Game.Stopped");
        await _messenger.SendAsync(session.ChatId, text, cancellationToken);

        _logger.LogInformation("Game stopped for chat {ChatId}. Session preserved for /standings.", session.ChatId);
    }

    private async Task CompleteTourAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Completing tour {Tour}/{MaxTours}. Active players: {ActiveCount}",
            session.CurrentTour, session.Tours, session.ActivePlayers.Count());

        // Show tour summary with current standings
        var summaryHeader = _localization.GetString(session.Language, "Game.TourSummary");
        var standingsText = BuildStandingsSummary(session);
        await _messenger.SendAsync(session.ChatId, $"{summaryHeader}\n{standingsText}", cancellationToken);

        session.TurnQueue.Clear();
        session.CurrentRound = 0;
        session.CurrentQuestion = null;
        session.CurrentPlayerId = null;

        // Check if we should skip sudden death checks (e.g., when coming from sudden death resolution)
        var skipSuddenDeathCheck = session.Metadata.ContainsKey("SkipSuddenDeathCheck");
        if (skipSuddenDeathCheck)
        {
            session.Metadata.Remove("SkipSuddenDeathCheck");
            _logger.LogInformation("Skipping sudden death check and moving directly to next tour");
        }

        // Use sudden death service to determine if sudden death is needed
        var decision = _suddenDeathService.DetermineIfSuddenDeathNeeded(session, skipSuddenDeathCheck);

        if (!decision.IsNeeded)
        {
            // No sudden death needed - handle normal elimination if applicable
            var activePlayers = session.ActivePlayers.ToList();
            if (activePlayers.Count > 1)
            {
                var minScore = activePlayers.Min(p => p.Score);
                var tiedForLowest = activePlayers
                    .Where(p => p.Score == minScore)
                    .ToList();

                var remainingAfterElimination = activePlayers.Count - tiedForLowest.Count;

                // Eliminate if it's safe to do so (would leave 3+ players)
                if (remainingAfterElimination >= 3)
                {
                    _logger.LogInformation("Eliminating {Count} player(s) tied for lowest score", tiedForLowest.Count);

                    foreach (var player in tiedForLowest)
                    {
                        player.Status = PlayerStatus.Eliminated;
                        _logger.LogInformation("Player {PlayerName} eliminated. Score: {Score}, Wrong answers: {Wrong}",
                            player.DisplayName, player.Score, player.IncorrectAnswers);
                        var text = string.Format(
                            _localization.GetString(session.Language, "Game.Eliminated"),
                            player.DisplayName);
                        await _messenger.SendAsync(session.ChatId, text, cancellationToken);
                    }
                }
                else if (tiedForLowest.Count == 1 && remainingAfterElimination >= 1)
                {
                    // Only one player with lowest score - eliminate them
                    var player = tiedForLowest[0];
                    player.Status = PlayerStatus.Eliminated;
                    _logger.LogInformation("Player {PlayerName} eliminated. Score: {Score}",
                        player.DisplayName, player.Score);
                    var text = string.Format(
                        _localization.GetString(session.Language, "Game.Eliminated"),
                        player.DisplayName);
                    await _messenger.SendAsync(session.ChatId, text, cancellationToken);
                }
            }
        }
        else
        {
            // Enter sudden death mode
            _logger.LogInformation("Entering sudden death: {Reason}", decision.Reason);

            _suddenDeathService.EnterSuddenDeath(session, decision.Participants);

            var text = _localization.GetString(session.Language, "Game.SuddenDeath");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);

            // Queue only sudden death participants
            foreach (var player in decision.Participants)
            {
                session.TurnQueue.Enqueue(player.Id);
            }

            await _repository.SaveAsync(session, cancellationToken);
            await AdvanceRoundAsync(session, cancellationToken);
            return;
        }

        session.CurrentTour += 1;

        if (session.CurrentTour > session.Tours || session.ActivePlayers.Count() <= 1)
        {
            _logger.LogInformation("Game ending condition met. Tour: {Tour}/{MaxTours}, Active: {Active}",
                session.CurrentTour, session.Tours, session.ActivePlayers.Count());
            await CompleteGameAsync(session, cancellationToken);
            return;
        }

        var nextTopic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
        _logger.LogInformation("Moving to tour {Tour} with topic: {Topic}", session.CurrentTour, nextTopic);

        var textTour = string.Format(
            _localization.GetString(session.Language, "Game.TourComplete"),
            session.CurrentTour - 1,
            nextTopic);
        await _messenger.SendAsync(session.ChatId, textTour, cancellationToken);

        // Prepare questions for next tour during the pause (if needed)
        await PrepareNextTourQuestionsAsync(session, cancellationToken);

        // Pause between tours if configured
        if (_gameOptions.TourPauseSeconds > 0)
        {
            _logger.LogInformation("Pausing for {Seconds} seconds before tour {Tour}",
                _gameOptions.TourPauseSeconds, session.CurrentTour);

            var pauseMessage = session.Language == GameLanguage.Russian
                ? $"⏸️ Следующий тур ({session.CurrentTour}) начнётся через {_gameOptions.TourPauseSeconds} секунд...\n\n" +
                  $"📍 Тема: {nextTopic}\n\n" +
                  $"Текущие результаты:\n{standingsText}"
                : $"⏸️ Next tour ({session.CurrentTour}) will start in {_gameOptions.TourPauseSeconds} seconds...\n\n" +
                  $"📍 Topic: {nextTopic}\n\n" +
                  $"Current standings:\n{standingsText}";

            await _messenger.SendAsync(session.ChatId, pauseMessage, cancellationToken);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_gameOptions.TourPauseSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Tour pause was cancelled");
            }
        }

        foreach (var player in session.ActivePlayers)
        {
            session.TurnQueue.Enqueue(player.Id);
        }

        await _repository.SaveAsync(session, cancellationToken);
        await AdvanceRoundAsync(session, cancellationToken);
    }

    private void StartAnswerTimer(long chatId, DateTimeOffset questionAskedAt, int timeoutSeconds)
    {
        var timerKey = (chatId, questionAskedAt);

        // Cancel any existing timer for this chat/question
        if (_answerTimers.TryRemove(timerKey, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _answerTimers[timerKey] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cts.Token);

                // Timer fired - handle timeout
                _logger.LogInformation("Answer timeout for chat {ChatId}, question at {AskedAt}", chatId, questionAskedAt);
                await HandleAnswerTimeoutAsync(chatId, questionAskedAt);
            }
            catch (OperationCanceledException)
            {
                // Timer was cancelled - this is expected when answer is received
                _logger.LogDebug("Answer timer cancelled for chat {ChatId}", chatId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in answer timer for chat {ChatId}", chatId);
            }
            finally
            {
                // Cleanup
                if (_answerTimers.TryRemove(timerKey, out var removedCts))
                {
                    removedCts.Dispose();
                }
            }
        }, cts.Token);
    }

    private void CancelAnswerTimer(long chatId, DateTimeOffset questionAskedAt)
    {
        var timerKey = (chatId, questionAskedAt);

        if (_answerTimers.TryRemove(timerKey, out var cts))
        {
            _logger.LogDebug("Cancelling answer timer for chat {ChatId}", chatId);
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task HandleAnswerTimeoutAsync(long chatId, DateTimeOffset questionAskedAt)
    {
        try
        {
            var session = await _repository.LoadAsync(chatId, CancellationToken.None);
            if (session == null)
            {
                _logger.LogWarning("Session not found for timeout handling. ChatId: {ChatId}", chatId);
                return;
            }

            // Verify this timeout is for the current question
            if (session.CurrentQuestionAskedAt != questionAskedAt ||
                session.CurrentQuestion == null ||
                session.CurrentPlayerId == null)
            {
                _logger.LogDebug("Timeout no longer relevant. Question already answered or changed.");
                return;
            }

            var player = session.FindPlayer(session.CurrentPlayerId.Value);
            if (player == null)
            {
                _logger.LogWarning("Player {PlayerId} not found for timeout", session.CurrentPlayerId.Value);
                return;
            }

            _logger.LogInformation("Processing timeout for player {PlayerName}. Question: {Question}",
                player.DisplayName, session.CurrentQuestion.Text);

            // Treat timeout as incorrect answer
            player.IncorrectAnswers += 1;

            var text = string.Format(
                _localization.GetString(session.Language, "Game.Timeout"),
                player.DisplayName,
                session.CurrentQuestion.Answer);

            await _messenger.SendAsync(session.ChatId, text, CancellationToken.None);

            // Clear current question and advance
            session.CurrentQuestion = null;
            session.CurrentPlayerId = null;
            session.CurrentQuestionAskedAt = null;
            session.CurrentQuestionMessageId = null;
            await _repository.SaveAsync(session, CancellationToken.None);

            await AdvanceRoundAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling answer timeout for chat {ChatId}", chatId);
        }
    }

    private async Task EnsureQuestionsAvailableAsync(GameSession session, CancellationToken cancellationToken)
    {
        // Determine how many questions we need in reserve based on game mode
        var scoreHandler = session.Status == GameStatus.SuddenDeath
            ? _suddenDeathScoreHandler
            : _regularScoreHandler;
        var (threshold, targetBuffer) = scoreHandler.GetQuestionThresholds();

        // Check current tour questions
        if (!session.QuestionsByTour.TryGetValue(session.CurrentTour, out var questions))
        {
            questions = new Queue<Question>();
            session.QuestionsByTour[session.CurrentTour] = questions;
        }

        if (questions.Count >= threshold)
        {
            // We have enough questions, no need to generate
            return;
        }

        _logger.LogInformation("Running low on questions for tour {Tour} (current: {Count}, threshold: {Threshold}). Checking pool first...",
            session.CurrentTour, questions.Count, threshold);

        try
        {
            var topic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
            var questionsNeeded = Math.Max(targetBuffer - questions.Count, targetBuffer);

            // PRIORITY 1: Try to get questions from unused pool (any topic)
            _logger.LogInformation("Attempting to get {Count} questions from unused pool before generating via API", questionsNeeded);
            var questionsFromPool = await _poolRepository.SelectQuestionsAsync(string.Empty, questionsNeeded, cancellationToken);

            if (questionsFromPool.Count > 0)
            {
                _logger.LogInformation("Found {Count} unused questions in pool. Adding to queue.", questionsFromPool.Count);

                foreach (var question in questionsFromPool)
                {
                    questions.Enqueue(question with { Topic = topic });
                }

                await _repository.SaveAsync(session, cancellationToken);

                // If we got enough questions from pool, we're done
                if (questions.Count >= threshold)
                {
                    var statusMessage = session.Language == GameLanguage.Russian
                        ? $"🔄 Добавлено {questionsFromPool.Count} вопросов из пула"
                        : $"🔄 Added {questionsFromPool.Count} questions from pool";

                    await _messenger.SendAsync(session.ChatId, statusMessage, cancellationToken);
                    return;
                }

                // Still need more, continue to generation
                questionsNeeded = Math.Max(targetBuffer - questions.Count, targetBuffer);
                _logger.LogInformation("Still need {Count} more questions. Generating via API...", questionsNeeded);
            }

            // PRIORITY 2: Generate via API if pool doesn't have enough
            var provider = _questionProviderFactory.Resolve(session.QuestionSourceMode);
            _logger.LogInformation("Generating {Count} new questions for topic '{Topic}'", questionsNeeded, topic);

            // Get archived questions from both session and pool repository to avoid repetition
            var sessionAskedQuestions = session.Metadata.TryGetValue("AskedQuestions", out var askedObj)
                ? ExtractAskedQuestions(askedObj)
                : new List<Question>();

            // Get archived questions filtered by current topic and recent months (last 2 months)
            // This is more efficient and focuses on preventing repetition from recent games
            var poolArchivedQuestions = await _poolRepository.GetArchivedQuestionsByTopicAsync(
                topic,
                maxMonthsBack: 1,  // Look back 2 months (current month + 1 month back)
                cancellationToken);

            // Combine both sources
            var allArchivedQuestions = new List<Question>(sessionAskedQuestions);
            allArchivedQuestions.AddRange(poolArchivedQuestions);

            _logger.LogInformation("Using {SessionCount} session questions + {PoolCount} archived questions from topic '{Topic}' for AI context",
                sessionAskedQuestions.Count, poolArchivedQuestions.Count, topic);

            // Notify chat that on-the-fly generation is starting
            var generatingMessage = session.Language == GameLanguage.Russian
                ? $"🤖 Генерирую дополнительные вопросы для тура {session.CurrentTour}: \"{topic}\"..."
                : $"🤖 Generating additional questions for tour {session.CurrentTour}: \"{topic}\"...";

            await _messenger.SendAsync(session.ChatId, generatingMessage, cancellationToken);

            // Generate questions via API
            IReadOnlyDictionary<int, List<Question>> generated;
            if (provider is AiQuestionProvider aiProvider)
            {
                generated = await aiProvider.PrepareQuestionPoolAsync(
                    new[] { topic },
                    1,
                    questionsNeeded,
                    session.Players,
                    session.Language,
                    session.MatureContent,
                    allArchivedQuestions,
                    cancellationToken);
            }
            else
            {
                generated = await provider.PrepareQuestionPoolAsync(
                    new[] { topic },
                    1,
                    questionsNeeded,
                    session.Players,
                    session.Language,
                    session.MatureContent,
                    cancellationToken);
            }

            var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
            _logger.LogInformation("Generated {Count} new questions. Adding to current tour queue.", generatedList.Count);

            // Get existing question texts to avoid duplicates
            // Include: (1) questions in queue (including pool questions just added), (2) already asked questions, (3) archived questions
            var existingQuestionTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add questions currently in queue (including pool questions we just added)
            foreach (var q in questions)
            {
                existingQuestionTexts.Add(q.Text.Trim().ToLowerInvariant());
            }

            // Add questions already asked in this session
            foreach (var q in sessionAskedQuestions)
            {
                existingQuestionTexts.Add(q.Text.Trim().ToLowerInvariant());
            }

            // Add archived questions from pool
            foreach (var q in poolArchivedQuestions)
            {
                existingQuestionTexts.Add(q.Text.Trim().ToLowerInvariant());
            }

            _logger.LogInformation("Checking against {Count} total existing questions (queue: {Queue}, session: {Session}, archived: {Archived}, pool added: {PoolAdded})",
                existingQuestionTexts.Count, questions.Count, sessionAskedQuestions.Count, poolArchivedQuestions.Count, questionsFromPool.Count);

            // Add ONLY generated questions (not pool questions - those were already added)
            int added = 0;
            int skipped = 0;
            foreach (var question in generatedList)
            {
                var normalizedText = question.Text.Trim().ToLowerInvariant();
                if (!existingQuestionTexts.Contains(normalizedText))
                {
                    questions.Enqueue(question with { Topic = topic });
                    existingQuestionTexts.Add(normalizedText);
                    added++;
                }
                else
                {
                    skipped++;
                    _logger.LogDebug("Skipping duplicate question: {Question}", question.Text);
                }
            }

            _logger.LogInformation("Added {Added} unique questions from API, skipped {Skipped} duplicates. Total from pool: {PoolCount}",
                added, skipped, questionsFromPool.Count);

            await _repository.SaveAsync(session, cancellationToken);

            var totalAdded = added + questionsFromPool.Count;
            var finalStatusMessage = session.Language == GameLanguage.Russian
                ? $"🔄 Добавлено {totalAdded} вопросов ({questionsFromPool.Count} из пула, {added} сгенерировано)"
                : $"🔄 Added {totalAdded} questions ({questionsFromPool.Count} from pool, {added} generated)";

            await _messenger.SendAsync(session.ChatId, finalStatusMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate questions on the fly for chat {ChatId}, tour {Tour}",
                session.ChatId, session.CurrentTour);

            // Don't throw - let the game continue with whatever questions remain
            // The game will end gracefully if it truly runs out
        }
    }

    private async Task PrepareNextTourQuestionsAsync(GameSession session, CancellationToken cancellationToken)
    {
        // Check if questions already exist for the current (next) tour
        if (session.QuestionsByTour.ContainsKey(session.CurrentTour) &&
            session.QuestionsByTour[session.CurrentTour].Count > 0)
        {
            _logger.LogDebug("Questions already prepared for tour {Tour}. Skipping preparation.", session.CurrentTour);
            return;
        }

        _logger.LogInformation("Preparing questions for tour {Tour} during pause", session.CurrentTour);

        // Calculate required questions based on active players
        var requiredPerTour = session.ActivePlayers.Count() * session.RoundsPerTour;

        // NEW STRATEGY: Check if there are ANY unused questions in the pool
        var poolStats = await _poolRepository.GetPoolStatsAsync(cancellationToken);
        _logger.LogInformation("Pool stats before tour {Tour}: {Unused} unused questions available",
            session.CurrentTour, poolStats.Unused);

        // Try to get questions from unused pool first (any topic)
        var questionsFromPool = await _poolRepository.SelectQuestionsAsync(string.Empty, requiredPerTour, cancellationToken);
        _logger.LogDebug("Found {Count} unused questions in pool for tour {Tour}",
            questionsFromPool.Count, session.CurrentTour);

        if (questionsFromPool.Count >= requiredPerTour)
        {
            // Enough questions in pool - use them
            // The questions already have topics assigned, so we keep them as-is
            session.QuestionsByTour[session.CurrentTour] = new Queue<Question>(questionsFromPool.Take(requiredPerTour));
            _logger.LogInformation("Using {Count} pooled questions for tour {Tour}", requiredPerTour, session.CurrentTour);
            await _repository.SaveAsync(session, cancellationToken);
            return;
        }

        // Not enough questions in pool - need to generate
        // Select topic with configured probability from Topics list vs random AI-generated
        var selectedTopic = AiQuestionProvider.SelectTopicWithProbability(
            session.Topics,
            _gameOptions.TopicSelectionProbability);
        var isRandomTopic = string.IsNullOrEmpty(selectedTopic);
        var topicDisplay = isRandomTopic
            ? (session.Language == GameLanguage.Russian ? "случайная тема" : "random topic")
            : selectedTopic;

        var probabilityPercent = (int)(_gameOptions.TopicSelectionProbability * 100);
        _logger.LogInformation(
            "Generating questions for tour {Tour}. Selected topic: '{Topic}' (random: {IsRandom}, probability: {Probability}% from list)",
            session.CurrentTour, topicDisplay, isRandomTopic, probabilityPercent);

        var generatingMessage = session.Language == GameLanguage.Russian
            ? $"🔄 Подготавливаю вопросы для следующего тура: \"{topicDisplay}\"..."
            : $"🔄 Preparing questions for next tour: \"{topicDisplay}\"...";

        await _messenger.SendAsync(session.ChatId, generatingMessage, cancellationToken);

        try
        {
            var provider = _questionProviderFactory.Resolve(session.QuestionSourceMode);

            // Get archived questions to avoid repetition
            var sessionAskedQuestions = session.Metadata.TryGetValue("AskedQuestions", out var askedObj)
                ? ExtractAskedQuestions(askedObj)
                : new List<Question>();

            var poolArchivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(cancellationToken);

            var allArchivedQuestions = new List<Question>(sessionAskedQuestions);
            allArchivedQuestions.AddRange(poolArchivedQuestions);

            IReadOnlyDictionary<int, List<Question>> generated;
            if (provider is AiQuestionProvider aiProvider)
            {
                generated = await aiProvider.PrepareQuestionPoolAsync(
                    new[] { selectedTopic },
                    1,
                    requiredPerTour,
                    session.Players,
                    session.Language,
                    session.MatureContent,
                    allArchivedQuestions,
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
            _logger.LogInformation("Generated {Count} questions for tour {Tour} with topic '{Topic}'",
                generatedList.Count, session.CurrentTour, topicDisplay);

            // Combine pool + generated questions
            var combined = new List<Question>(questionsFromPool);
            combined.AddRange(generatedList);

            // Use the questions for this tour
            session.QuestionsByTour[session.CurrentTour] = new Queue<Question>(
                combined.Take(requiredPerTour)
            );

            // Store ALL surplus generated questions in unused pool for future tours
            if (generatedList.Count > requiredPerTour - questionsFromPool.Count)
            {
                var surplus = generatedList.Skip(requiredPerTour - questionsFromPool.Count).ToList();
                if (surplus.Count > 0)
                {
                    await _poolRepository.AddToUnusedPoolAsync(surplus, cancellationToken);
                    _logger.LogDebug("Added {Count} surplus questions to unused pool", surplus.Count);
                }
            }

            await _repository.SaveAsync(session, cancellationToken);

            var readyMessage = session.Language == GameLanguage.Russian
                ? $"✅ Вопросы для тура {session.CurrentTour} готовы!"
                : $"✅ Questions for tour {session.CurrentTour} ready!";

            await _messenger.SendAsync(session.ChatId, readyMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare questions for tour {Tour}. Will retry on-the-fly if needed.", session.CurrentTour);
            // Don't throw - the game will generate questions on-the-fly if needed
        }
    }

    private string BuildStandingsSummary(GameSession session)
    {
        var players = session.ActivePlayers
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.IncorrectAnswers)
            .ToList();

        var pointsWord = _localization.GetString(session.Language, "Game.Points");
        var lines = new List<string>();

        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            var position = i + 1;
            var emoji = position switch
            {
                1 => "🥇",
                2 => "🥈",
                3 => "🥉",
                _ => $"{position}."
            };

            lines.Add($"{emoji} {player.DisplayName}: {player.Score} {pointsWord}");
        }

        return string.Join("\n", lines);
    }

    private async Task CompleteGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Completing game for chat {ChatId}", session.ChatId);

        session.Status = GameStatus.Completed;
        session.CompletedAt = DateTimeOffset.UtcNow;

        // Archive used questions
        if (session.Metadata.TryGetValue("AskedQuestions", out var askedObj))
        {
            var askedQuestions = ExtractAskedQuestions(askedObj);
            if (askedQuestions.Count > 0)
            {
                try
                {
                    await _poolRepository.MoveToArchiveAsync(askedQuestions, cancellationToken);
                    _logger.LogInformation("Archived {Count} used questions from game in chat {ChatId}",
                        askedQuestions.Count, session.ChatId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to archive used questions for chat {ChatId}", session.ChatId);
                }
            }
        }

        // Determine winner - if coming from sudden death, use SuddenDeathScore as tiebreaker
        var activePlayers = session.ActivePlayers.ToList();
        Player? winner = null;

        if (activePlayers.Count > 0)
        {
            // Check if we just exited sudden death (indicated by any player having a non-zero SuddenDeathScore)
            var hasSuddenDeathScores = activePlayers.Any(p => p.SuddenDeathScore > 0);

            if (hasSuddenDeathScores)
            {
                // Use sudden death score as primary, then main score as tiebreaker
                winner = activePlayers
                    .OrderByDescending(p => p.SuddenDeathScore)
                    .ThenByDescending(p => p.Score)
                    .FirstOrDefault();
                _logger.LogInformation("Game winner (via sudden death): {PlayerName} with main score {Score}, sudden death score {SDScore}",
                    winner?.DisplayName, winner?.Score, winner?.SuddenDeathScore);
            }
            else
            {
                // Normal winner determination by main score
                winner = activePlayers.OrderByDescending(p => p.Score).FirstOrDefault();
                _logger.LogInformation("Game winner: {PlayerName} with score {Score}", winner?.DisplayName, winner?.Score);
            }
        }
        else
        {
            _logger.LogWarning("Game completed with no active players");
        }

        // Create and archive game result
        try
        {
            var gameResult = CreateGameResult(session);
            await _resultRepository.ArchiveAsync(gameResult, cancellationToken);
            _logger.LogInformation("Archived result for completed game {GameId} in chat {ChatId}", gameResult.GameId, session.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive game result for chat {ChatId}", session.ChatId);
        }

        // Save the completed session (don't delete it - keep it for /standings)
        // Session will be cleared when /start is called for a new game
        await _repository.SaveAsync(session, cancellationToken);

        var text = winner is null
            ? _localization.GetString(session.Language, "Game.Stopped")
            : string.Format(_localization.GetString(session.Language, "Game.Completed"), winner.DisplayName);

        await _messenger.SendAsync(session.ChatId, text, cancellationToken);

        _logger.LogInformation("Game finalized successfully for chat {ChatId}. Session preserved for /standings.", session.ChatId);
    }

    private static string Normalize(string value)
    {
        return value.Trim().Trim('.', '!', '?', '\'', '"');
    }

    private static List<Question> ExtractAskedQuestions(object askedObj)
    {
        // Handle both direct List<Question> and JsonElement cases (from deserialization)
        if (askedObj is List<Question> questions)
        {
            return questions;
        }

        if (askedObj is System.Text.Json.JsonElement jsonElement &&
            jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<Question>>(
                    jsonElement.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new List<Question>();
            }
            catch
            {
                return new List<Question>();
            }
        }

        return new List<Question>();
    }

    private static GameResult CreateGameResult(GameSession session)
    {
        var askedQuestions = session.Metadata.TryGetValue("AskedQuestions", out var askedObj)
            ? ExtractAskedQuestions(askedObj)
            : new List<Question>();

        var allPlayers = session.Players.OrderByDescending(p => p.Score).ToList();
        var activePlayers = session.ActivePlayers.ToList();

        // Assign placements to active players
        var playerResults = new List<PlayerResult>();
        int placement = 1;

        foreach (var player in allPlayers)
        {
            var playerResult = new PlayerResult
            {
                Id = player.Id,
                DisplayName = player.DisplayName,
                Score = player.Score,
                CorrectAnswers = player.CorrectAnswers,
                IncorrectAnswers = player.IncorrectAnswers,
                FinalStatus = player.Status,
                Placement = player.Status == PlayerStatus.Active ? placement++ : null
            };
            playerResults.Add(playerResult);
        }

        var totalAnswers = allPlayers.Sum(p => p.CorrectAnswers + p.IncorrectAnswers);
        var totalCorrect = allPlayers.Sum(p => p.CorrectAnswers);

        var statistics = new GameStatistics
        {
            TotalQuestions = askedQuestions.Count,
            ToursCompleted = session.CurrentTour - 1,
            PlayersStarted = session.Players.Count,
            PlayersEliminated = session.Players.Count(p => p.Status == PlayerStatus.Eliminated),
            PlayersFinished = activePlayers.Count,
            AverageScore = allPlayers.Count > 0 ? allPlayers.Average(p => p.Score) : 0,
            AverageAccuracy = totalAnswers > 0 ? (double)totalCorrect / totalAnswers : 0
        };

        return new GameResult
        {
            GameId = session.Id.ToString(),
            ChatId = session.ChatId,
            Language = session.Language,
            QuestionSourceMode = session.QuestionSourceMode,
            Topics = session.Topics.ToArray(),
            Tours = session.Tours,
            RoundsPerTour = session.RoundsPerTour,
            FinalStatus = session.Status,
            StartedAt = session.StartedAt ?? DateTimeOffset.UtcNow,
            CompletedAt = session.CompletedAt ?? DateTimeOffset.UtcNow,
            Duration = (session.CompletedAt ?? DateTimeOffset.UtcNow) - (session.StartedAt ?? DateTimeOffset.UtcNow),
            Players = playerResults,
            UsedQuestions = askedQuestions,
            Statistics = statistics
        };
    }

    public async Task PauseGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("PauseGameAsync called for chat {ChatId}. Current status: {Status}",
            session.ChatId, session.Status);

        if (session.Status != GameStatus.InProgress && session.Status != GameStatus.SuddenDeath)
        {
            _logger.LogWarning("Cannot pause game in status {Status}", session.Status);
            var text = "⚠️ Игра не активна. Нельзя поставить на паузу.";
            if (session.Language == GameLanguage.English)
            {
                text = "⚠️ Game is not active. Cannot pause.";
            }
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        // Save the current status so we can restore it on resume
        session.Metadata["PausedFromStatus"] = session.Status.ToString();

        // Save the pause timestamp
        session.Metadata["PausedAt"] = DateTimeOffset.UtcNow.ToString("o");

        // If there's an active timer, cancel it and save remaining time
        if (session.CurrentQuestionAskedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - session.CurrentQuestionAskedAt.Value;
            var remaining = _gameOptions.AnswerTimeoutSeconds - elapsed.TotalSeconds;

            if (remaining > 0)
            {
                session.Metadata["RemainingAnswerTime"] = remaining.ToString();
                _logger.LogInformation("Saved remaining answer time: {Remaining}s", remaining);
            }

            // Cancel the active timer
            var timerKey = (session.ChatId, session.CurrentQuestionAskedAt.Value);
            if (_answerTimers.TryRemove(timerKey, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _logger.LogDebug("Cancelled answer timer for chat {ChatId}", session.ChatId);
            }
        }

        session.Status = GameStatus.Paused;
        await _repository.SaveAsync(session, cancellationToken);

        var pausedText = _localization.GetString(session.Language, "Game.Paused");
        await _messenger.SendAsync(session.ChatId, pausedText, cancellationToken);

        _logger.LogInformation("Game paused for chat {ChatId}", session.ChatId);
    }

    public async Task ResumeGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("ResumeGameAsync called for chat {ChatId}. Current status: {Status}",
            session.ChatId, session.Status);

        if (session.Status != GameStatus.Paused)
        {
            _logger.LogWarning("Cannot resume game in status {Status}", session.Status);
            var text = "⚠️ Игра не на паузе. Нельзя продолжить.";
            if (session.Language == GameLanguage.English)
            {
                text = "⚠️ Game is not paused. Cannot resume.";
            }
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        // Restore the previous status
        if (session.Metadata.TryGetValue("PausedFromStatus", out var statusObj) &&
            statusObj is string statusStr &&
            Enum.TryParse<GameStatus>(statusStr, out var previousStatus))
        {
            session.Status = previousStatus;
            session.Metadata.Remove("PausedFromStatus");
            _logger.LogInformation("Restored status to {Status}", previousStatus);
        }
        else
        {
            session.Status = GameStatus.InProgress;
            _logger.LogWarning("Could not restore previous status, defaulting to InProgress");
        }

        // Remove pause timestamp
        session.Metadata.Remove("PausedAt");

        // If there's a current question with remaining time, restart the timer
        if (session.CurrentQuestionAskedAt.HasValue &&
            session.Metadata.TryGetValue("RemainingAnswerTime", out var remainingObj) &&
            remainingObj is string remainingStr &&
            double.TryParse(remainingStr, out var remainingSeconds))
        {
            session.Metadata.Remove("RemainingAnswerTime");

            // Adjust the question asked time to reflect the pause
            var adjustedTime = DateTimeOffset.UtcNow.AddSeconds(-(_gameOptions.AnswerTimeoutSeconds - remainingSeconds));
            session.CurrentQuestionAskedAt = adjustedTime;

            _logger.LogInformation("Restarting answer timer with {Remaining}s remaining", remainingSeconds);
            StartAnswerTimer(session.ChatId, adjustedTime, (int)Math.Ceiling(remainingSeconds));
        }

        await _repository.SaveAsync(session, cancellationToken);

        var resumedText = _localization.GetString(session.Language, "Game.Resumed");
        await _messenger.SendAsync(session.ChatId, resumedText, cancellationToken);

        _logger.LogInformation("Game resumed for chat {ChatId}", session.ChatId);
    }
}

