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

        if (session.Status == GameStatus.InProgress || session.Status == GameStatus.SuddenDeath)
        {
            _logger.LogWarning("Cannot start game for chat {ChatId} - game already in progress (Status: {Status})", session.ChatId, session.Status);
            var text = session.Language == GameLanguage.Russian
                ? "⚠️ Игра уже идёт. Используйте /stop чтобы остановить текущую игру."
                : "⚠️ A game is already in progress. Use /stop to end the current game.";
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        if (session.Players.Count < 1)
        {
            _logger.LogWarning("Not enough players to start game. Players: {PlayerCount}", session.Players.Count);
            var text = _localization.GetString(session.Language, "Game.NotEnoughPlayers");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        // Count actual questions, not tour keys. A tour key can exist while mapping to an empty
        // queue (e.g. question generation failed during pool prep). Guarding on .Count here would
        // let such a session pass, announce "game started", flip to InProgress, and then stall in
        // AdvanceRoundAsync with nothing to ask — the game looks started but never progresses.
        if (session.QuestionsByTour.Values.Sum(q => q.Count) == 0)
        {
            _logger.LogWarning("No question pool available for chat {ChatId} (tour keys: {Keys}, total questions: 0)",
                session.ChatId, session.QuestionsByTour.Count);
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

        // Idempotency guard: a question is already posed and awaiting an answer. Advancing now would
        // skip the current player's turn and burn a question without anyone answering it. The round
        // is only ever advanced in response to an answer or a timeout (both of which clear
        // CurrentQuestion first), so a pending question means this is a spurious/duplicate call —
        // e.g. an answer landing at the same moment its timeout fires. Just return.
        if (session.CurrentQuestion is not null)
        {
            _logger.LogDebug("AdvanceRoundAsync called while a question is still pending for chat {ChatId}. Ignoring duplicate advance.",
                session.ChatId);
            return;
        }

        // Check if we need to generate more questions
        await EnsureQuestionsAvailableAsync(session, cancellationToken);

        if (!session.QuestionsByTour.TryGetValue(session.CurrentTour, out var questions))
        {
            questions = new Queue<Question>();
            session.QuestionsByTour[session.CurrentTour] = questions;
        }

        // NOTE: do NOT bail here just because the queue is empty. End-of-round processing
        // (sudden-death resolution, elimination, tour completion) does not need a *new* question
        // and must run first — otherwise a resolvable sudden death whose queue happens to be empty
        // falls through to CompleteTour, which re-enters sudden death on the same tour (scores still
        // tied) and recurses AdvanceRound ⇄ CompleteTour forever. The no-questions check now lives
        // just before we actually need to ask a question (further below).

        if (session.TurnQueue.Count == 0)
        {
            // In sudden death mode, check if ties are resolved after each round
            if (session.Status == GameStatus.SuddenDeath)
            {
                // Get the sudden death starting round from metadata
                int startRound;
                if (session.Metadata.TryGetValue("SuddenDeathStartRound", out var startRoundObj))
                {
                    if (startRoundObj is int directInt)
                        startRound = directInt;
                    else if (startRoundObj is long directLong)
                        startRound = (int)directLong;
                    else if (startRoundObj is System.Text.Json.JsonElement je &&
                             je.ValueKind == System.Text.Json.JsonValueKind.Number)
                        startRound = je.GetInt32();
                    else
                    {
                        _logger.LogError("SuddenDeathStartRound has unexpected type {Type}. Using fallback.", startRoundObj?.GetType().Name);
                        startRound = session.CurrentRound;
                    }
                }
                else
                {
                    _logger.LogError("SuddenDeathStartRound not found in metadata! This should not happen.");
                    startRound = session.CurrentRound;
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

        // We have a player to ask but no question to ask them. Complete the tour, but guard against
        // an infinite AdvanceRound ⇄ CompleteTour loop: if CompleteTour keeps the game on the same
        // tour (e.g. re-entering sudden death while scores stay tied) and the queue is still empty
        // on the next pass, the streak counter trips and we end the game. Counting *consecutive*
        // empty completions catches this even when the tour number never advances.
        if (questions.Count == 0)
        {
            _logger.LogInformation("No questions remaining for tour {Tour} after end-of-round processing.", session.CurrentTour);

            var emptyStreak = GetNoQuestionsStreak(session) + 1;
            if (emptyStreak >= 2)
            {
                _logger.LogWarning("Detected {Streak} consecutive tour completions with no questions. Ending game to prevent infinite loop. Tour: {Tour}",
                    emptyStreak, session.CurrentTour);
                session.Metadata.Remove("NoQuestionsStreak");
                await CompleteGameAsync(session, cancellationToken);
                return;
            }

            session.Metadata["NoQuestionsStreak"] = emptyStreak;
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        // A question is available — reset the empty-completion streak.
        session.Metadata.Remove("NoQuestionsStreak");

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

        var inSuddenDeath = session.Status == GameStatus.SuddenDeath;
        if (isCorrect)
        {
            var scoreHandler = inSuddenDeath ? _suddenDeathScoreHandler : _regularScoreHandler;
            scoreHandler.UpdateScore(player, isCorrect: true);
            // CorrectAnswers/IncorrectAnswers track regular-game stats only
            if (!inSuddenDeath) player.CorrectAnswers += 1;
            var text = _localization.GetString(session.Language, "Game.Correct");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        }
        else
        {
            if (!inSuddenDeath) player.IncorrectAnswers += 1;
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
        if (session.Status == GameStatus.Cancelled || session.Status == GameStatus.Completed)
        {
            _logger.LogWarning("StopGameAsync called for chat {ChatId} but game is already stopped (Status: {Status}). Ignoring.", session.ChatId, session.Status);
            return;
        }

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

                // Eliminate if safe: leaves 3+ players, or leaves exactly 1 winner (all tied are last place)
                if (remainingAfterElimination >= 3 || remainingAfterElimination == 1)
                {
                    _logger.LogInformation("Eliminating {Count} player(s) tied for lowest score (leaves {Remaining})", tiedForLowest.Count, remainingAfterElimination);

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

            // Treat timeout as incorrect answer — don't count stats during sudden death
            if (session.Status != GameStatus.SuddenDeath) player.IncorrectAnswers += 1;

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

    // Tracks chats that already have a background generation task running, to avoid duplicates.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> _generatingChats = new();

    private async Task EnsureQuestionsAvailableAsync(GameSession session, CancellationToken cancellationToken)
    {
        var scoreHandler = session.Status == GameStatus.SuddenDeath
            ? _suddenDeathScoreHandler
            : _regularScoreHandler;
        var (threshold, targetBuffer) = scoreHandler.GetQuestionThresholds();

        if (!session.QuestionsByTour.TryGetValue(session.CurrentTour, out var questions))
        {
            questions = new Queue<Question>();
            session.QuestionsByTour[session.CurrentTour] = questions;
        }

        if (questions.Count >= threshold)
            return;

        _logger.LogInformation("Running low on questions for tour {Tour} (current: {Count}, threshold: {Threshold}). Checking pool first...",
            session.CurrentTour, questions.Count, threshold);

        try
        {
            // In sudden death the topic list may be exhausted — pick a random configured topic instead
            // of falling back to the synthetic "Topic N" placeholder.
            var isSuddenDeath = session.Status == GameStatus.SuddenDeath;
            var topic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1);
            if (string.IsNullOrWhiteSpace(topic))
            {
                var pool = _gameOptions.Topics.Length > 0 ? _gameOptions.Topics : new[] { "General" };
                topic = pool[Random.Shared.Next(pool.Length)];
                _logger.LogInformation("Topic list exhausted for tour {Tour} (sudden death: {IsSd}). Using random topic '{Topic}'.",
                    session.CurrentTour, isSuddenDeath, topic);
            }

            var questionsNeeded = Math.Max(targetBuffer - questions.Count, targetBuffer);

            // PRIORITY 1: pool
            _logger.LogInformation("Attempting to get {Count} topic-specific questions from pool for '{Topic}'", questionsNeeded, topic);

            var sessionAskedForDedup = session.Metadata.TryGetValue("AskedQuestions", out var dedupObj)
                ? ExtractAskedQuestions(dedupObj)
                : new List<Question>();
            var dedupTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dedupAnswers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in sessionAskedForDedup) { dedupTexts.Add(q.Text.Trim().ToLowerInvariant()); dedupAnswers.Add(q.Answer.Trim().ToLowerInvariant()); }
            foreach (var q in questions) { dedupTexts.Add(q.Text.Trim().ToLowerInvariant()); dedupAnswers.Add(q.Answer.Trim().ToLowerInvariant()); }

            var rawFromPool = await _poolRepository.SelectQuestionsAsync(topic, questionsNeeded, cancellationToken);
            var questionsFromPool = rawFromPool
                .Where(q => !dedupTexts.Contains(q.Text.Trim().ToLowerInvariant()) && !dedupAnswers.Contains(q.Answer.Trim().ToLowerInvariant()))
                .ToList();

            if (questionsFromPool.Count > 0)
            {
                _logger.LogInformation("Found {Count} non-duplicate topic questions in pool ({Raw} raw). Adding to queue.", questionsFromPool.Count, rawFromPool.Count);

                foreach (var question in questionsFromPool)
                {
                    questions.Enqueue(question);
                    dedupTexts.Add(question.Text.Trim().ToLowerInvariant());
                    dedupAnswers.Add(question.Answer.Trim().ToLowerInvariant());
                }

                await _repository.SaveAsync(session, cancellationToken);

                if (questions.Count >= threshold)
                {
                    var statusMessage = session.Language == GameLanguage.Russian
                        ? $"🔄 Добавлено {questionsFromPool.Count} вопросов из пула"
                        : $"🔄 Added {questionsFromPool.Count} questions from pool";
                    await _messenger.SendAsync(session.ChatId, statusMessage, cancellationToken);
                    return;
                }

                questionsNeeded = Math.Max(targetBuffer - questions.Count, targetBuffer);
                _logger.LogInformation("Still need {Count} more questions. Scheduling background generation...", questionsNeeded);
            }

            // PRIORITY 2: API generation.
            // If the queue is completely empty we MUST generate synchronously — returning with 0
            // questions causes CompleteTour → AdvanceRound → CompleteTour infinite recursion.
            // If a background generation is already running for this chat, wait up to 30s for it
            // to deposit questions into the pool before launching a competing synchronous call.
            if (questions.Count == 0)
            {
                if (_generatingChats.ContainsKey(session.ChatId))
                {
                    _logger.LogInformation("Queue empty for tour {Tour} but background generation is running — waiting up to 30s for pool.", session.CurrentTour);
                    for (var waited = 0; waited < 30 && _generatingChats.ContainsKey(session.ChatId); waited++)
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                    // Re-check pool after waiting
                    var rawAfterWait = await _poolRepository.SelectQuestionsAsync(topic, questionsNeeded, cancellationToken);
                    var afterWait = rawAfterWait
                        .Where(q => !dedupTexts.Contains(q.Text.Trim().ToLowerInvariant()) && !dedupAnswers.Contains(q.Answer.Trim().ToLowerInvariant()))
                        .ToList();
                    if (afterWait.Count > 0)
                    {
                        _logger.LogInformation("Background generation delivered {Count} questions after wait. Using them.", afterWait.Count);
                        foreach (var q in afterWait) questions.Enqueue(q);
                        await _repository.SaveAsync(session, cancellationToken);
                        return;
                    }
                    _logger.LogWarning("Background generation did not deliver questions in time. Falling back to synchronous generation.");
                }

                _logger.LogInformation("Queue is empty for tour {Tour} — generating synchronously to avoid infinite loop.", session.CurrentTour);

                var provider = _questionProviderFactory.Resolve(session.QuestionSourceMode);
                var sessionAskedSync = session.Metadata.TryGetValue("AskedQuestions", out var syncAskedObj)
                    ? ExtractAskedQuestions(syncAskedObj) : new List<Question>();
                var archivedSync = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(topic, cancellationToken);
                var allArchivedSync = new List<Question>(sessionAskedSync);
                allArchivedSync.AddRange(archivedSync);

                IReadOnlyDictionary<int, List<Question>> syncGenerated;
                if (provider is AiQuestionProvider aiProviderSync)
                {
                    syncGenerated = await aiProviderSync.PrepareQuestionPoolAsync(
                        new[] { topic }, 1, questionsNeeded, Array.Empty<Player>(), session.Language,
                        session.MatureContent, allArchivedSync, session.DifficultyLevel, cancellationToken);
                }
                else
                {
                    syncGenerated = await provider.PrepareQuestionPoolAsync(
                        new[] { topic }, 1, questionsNeeded, Array.Empty<Player>(), session.Language,
                        session.MatureContent, cancellationToken);
                }

                var syncList = syncGenerated.Values.FirstOrDefault() ?? new List<Question>();
                _logger.LogInformation("Synchronous generation produced {Count} questions for empty queue.", syncList.Count);

                foreach (var q in syncList)
                {
                    var key = q.Text.Trim().ToLowerInvariant();
                    var answerKey = q.Answer.Trim().ToLowerInvariant();
                    if (!dedupTexts.Contains(key) && !dedupAnswers.Contains(answerKey))
                    {
                        questions.Enqueue(q with { Topic = topic });
                        dedupTexts.Add(key);
                        dedupAnswers.Add(answerKey);
                    }
                }

                await _repository.SaveAsync(session, cancellationToken);

                if (questions.Count > 0)
                    return;

                // Primary topic generation yielded nothing — try a fallback topic.
                _logger.LogWarning("Primary topic '{Topic}' generation returned no questions. Attempting fallback topic.", topic);
                var fallbackTopic = PickFallbackTopic(topic, session.Topics, _gameOptions.Topics);
                _logger.LogInformation("Fallback topic selected: '{FallbackTopic}'", fallbackTopic);

                var fallbackNotice = session.Language == GameLanguage.Russian
                    ? $"⚠️ Не удалось получить вопросы по теме «{topic}». По техническим причинам меняем тему на «{fallbackTopic}»."
                    : $"⚠️ Could not get questions for topic \"{topic}\". Switching to \"{fallbackTopic}\" for technical reasons.";
                await _messenger.SendAsync(session.ChatId, fallbackNotice, cancellationToken);

                var archivedFallback = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(fallbackTopic, cancellationToken);
                var allArchivedFallback = new List<Question>(sessionAskedSync);
                allArchivedFallback.AddRange(archivedFallback);

                IReadOnlyDictionary<int, List<Question>> fallbackGenerated;
                if (provider is AiQuestionProvider aiProviderFallback)
                {
                    fallbackGenerated = await aiProviderFallback.PrepareQuestionPoolAsync(
                        new[] { fallbackTopic }, 1, questionsNeeded, Array.Empty<Player>(), session.Language,
                        session.MatureContent, allArchivedFallback, session.DifficultyLevel, cancellationToken);
                }
                else
                {
                    fallbackGenerated = await provider.PrepareQuestionPoolAsync(
                        new[] { fallbackTopic }, 1, questionsNeeded, Array.Empty<Player>(), session.Language,
                        session.MatureContent, cancellationToken);
                }

                var fallbackList = fallbackGenerated.Values.FirstOrDefault() ?? new List<Question>();
                _logger.LogInformation("Fallback generation produced {Count} questions for topic '{Topic}'.", fallbackList.Count, fallbackTopic);

                foreach (var q in fallbackList)
                {
                    var key = q.Text.Trim().ToLowerInvariant();
                    var answerKey = q.Answer.Trim().ToLowerInvariant();
                    if (!dedupTexts.Contains(key) && !dedupAnswers.Contains(answerKey))
                    {
                        questions.Enqueue(q with { Topic = fallbackTopic });
                        dedupTexts.Add(key);
                        dedupAnswers.Add(answerKey);
                    }
                }

                await _repository.SaveAsync(session, cancellationToken);

                if (questions.Count == 0)
                {
                    var giveUpMessage = session.Language == GameLanguage.Russian
                        ? "⚠️ Не удалось получить вопросы. Продолжаем с имеющимися."
                        : "⚠️ Could not fetch questions. Continuing with remaining ones.";
                    await _messenger.SendAsync(session.ChatId, giveUpMessage, cancellationToken);
                }

                return;
            }

            if (!_generatingChats.TryAdd(session.ChatId, true))
            {
                _logger.LogInformation("Background generation already running for chat {ChatId}. Skipping duplicate.", session.ChatId);
                return;
            }

            var sessionAskedQuestions = session.Metadata.TryGetValue("AskedQuestions", out var askedObj)
                ? ExtractAskedQuestions(askedObj)
                : new List<Question>();

            var poolArchivedQuestions = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(
                topic, cancellationToken);

            var allArchivedQuestions = new List<Question>(sessionAskedQuestions);
            allArchivedQuestions.AddRange(poolArchivedQuestions);

            _logger.LogInformation("Using {SessionCount} session questions + {PoolCount} archived questions from topic '{Topic}' for AI context",
                sessionAskedQuestions.Count, poolArchivedQuestions.Count, topic);

            // Capture values needed inside the background task (avoid capturing mutable session state).
            var chatId = session.ChatId;
            var language = session.Language;
            var matureContent = session.MatureContent;
            var difficultyLevel = session.DifficultyLevel;
            var sourceMode = session.QuestionSourceMode;
            var currentTour = session.CurrentTour;
            var questionsNeededCapture = questionsNeeded;
            var topicCapture = topic;
            var allArchivedCapture = allArchivedQuestions;
            var sessionTopicsCapture = session.Topics.ToArray();

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Background generation started for chat {ChatId}, tour {Tour}, topic '{Topic}'",
                        chatId, currentTour, topicCapture);

                    var provider = _questionProviderFactory.Resolve(sourceMode);

                    IReadOnlyDictionary<int, List<Question>> generated;
                    if (provider is AiQuestionProvider aiProvider)
                    {
                        generated = await aiProvider.PrepareQuestionPoolAsync(
                            new[] { topicCapture },
                            1,
                            questionsNeededCapture,
                            Array.Empty<Player>(),
                            language,
                            matureContent,
                            allArchivedCapture,
                            difficultyLevel,
                            CancellationToken.None);
                    }
                    else
                    {
                        generated = await provider.PrepareQuestionPoolAsync(
                            new[] { topicCapture },
                            1,
                            questionsNeededCapture,
                            Array.Empty<Player>(),
                            language,
                            matureContent,
                            CancellationToken.None);
                    }

                    var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                    _logger.LogInformation("Background generation produced {Count} questions for chat {ChatId}", generatedList.Count, chatId);

                    // Check if the game is still running (quick status check without loading full session)
                    var liveSession = await _repository.LoadAsync(chatId, CancellationToken.None);
                    if (liveSession == null ||
                        liveSession.Status == GameStatus.Completed ||
                        liveSession.Status == GameStatus.Cancelled)
                    {
                        _logger.LogInformation("Session gone or finished by the time background generation completed. Discarding {Count} questions.", generatedList.Count);
                        return;
                    }

                    if (generatedList.Count > 0)
                    {
                        // Always add generated questions to the shared pool rather than writing directly
                        // into the session queue. Writing into the session requires a SaveAsync which races
                        // with the foreground (especially during sudden death where SD metadata would be
                        // overwritten by the stale deserialized liveSession). The pool is safe to write
                        // concurrently; EnsureQuestionsAvailableAsync will pull from it on the next round.
                        await _poolRepository.AddToUnusedPoolAsync(generatedList, CancellationToken.None);
                        _logger.LogInformation("Background generation added {Count} questions to pool for chat {ChatId}.", generatedList.Count, chatId);
                        return;
                    }

                    // Background generation returned nothing — attempt a fallback topic silently
                    // and add its questions to the pool so the next round can pick them up.
                    _logger.LogWarning("Background generation for topic '{Topic}' returned 0 questions. Trying fallback topic for chat {ChatId}.",
                        topicCapture, chatId);

                    var bgFallbackTopic = PickFallbackTopic(topicCapture, sessionTopicsCapture, _gameOptions.Topics);
                    _logger.LogInformation("Background fallback topic: '{FallbackTopic}' for chat {ChatId}", bgFallbackTopic, chatId);

                    var fallbackNotice = language == GameLanguage.Russian
                        ? $"⚠️ Не удалось получить вопросы по теме «{topicCapture}». По техническим причинам меняем тему на «{bgFallbackTopic}»."
                        : $"⚠️ Could not get questions for topic \"{topicCapture}\". Switching to \"{bgFallbackTopic}\" for technical reasons.";
                    await _messenger.SendAsync(chatId, fallbackNotice, CancellationToken.None);

                    IReadOnlyDictionary<int, List<Question>> bgFallbackGenerated;
                    if (provider is AiQuestionProvider aiProviderBg)
                    {
                        bgFallbackGenerated = await aiProviderBg.PrepareQuestionPoolAsync(
                            new[] { bgFallbackTopic }, 1, questionsNeededCapture, Array.Empty<Player>(), language,
                            matureContent, allArchivedCapture, difficultyLevel, CancellationToken.None);
                    }
                    else
                    {
                        bgFallbackGenerated = await provider.PrepareQuestionPoolAsync(
                            new[] { bgFallbackTopic }, 1, questionsNeededCapture, Array.Empty<Player>(), language,
                            matureContent, CancellationToken.None);
                    }

                    var bgFallbackList = bgFallbackGenerated.Values.FirstOrDefault() ?? new List<Question>();
                    _logger.LogInformation("Background fallback generation produced {Count} questions for chat {ChatId}", bgFallbackList.Count, chatId);

                    if (bgFallbackList.Count > 0)
                    {
                        var topicTagged = bgFallbackList.Select(q => q with { Topic = bgFallbackTopic }).ToList();
                        await _poolRepository.AddToUnusedPoolAsync(topicTagged, CancellationToken.None);
                        _logger.LogInformation("Background fallback added {Count} questions to pool for chat {ChatId}.", topicTagged.Count, chatId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background question generation failed for chat {ChatId}, tour {Tour}", chatId, currentTour);
                }
                finally
                {
                    _generatingChats.TryRemove(chatId, out _);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure questions for chat {ChatId}, tour {Tour}",
                session.ChatId, session.CurrentTour);
        }
    }

    /// <summary>
    /// Picks a fallback topic that is different from <paramref name="currentTopic"/>.
    /// Prefers configured topics; falls back to any available topic.
    /// </summary>
    private static string PickFallbackTopic(string currentTopic, IReadOnlyList<string> sessionTopics, string[] configuredTopics)
    {
        // Prefer configured topics different from the failed one
        var candidates = configuredTopics
            .Where(t => !string.Equals(t, currentTopic, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count > 0)
            return candidates[Random.Shared.Next(candidates.Count)];

        // Fall back to session topics
        var sessionCandidates = sessionTopics
            .Where(t => !string.Equals(t, currentTopic, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sessionCandidates.Count > 0)
            return sessionCandidates[Random.Shared.Next(sessionCandidates.Count)];

        // Absolute last resort: use a fixed generic topic
        return "General";
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

        // Use the pre-assigned topic for this tour
        var topic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";

        // Build a set of all question texts already known to this session (asked + in any queue)
        var sessionAskedForDedup = session.Metadata.TryGetValue("AskedQuestions", out var askedObjDedup)
            ? ExtractAskedQuestions(askedObjDedup)
            : new List<Question>();
        var usedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in sessionAskedForDedup) usedTexts.Add(q.Text.Trim().ToLowerInvariant());
        foreach (var queue in session.QuestionsByTour.Values)
            foreach (var q in queue) usedTexts.Add(q.Text.Trim().ToLowerInvariant());

        // Try to get topic-specific questions from pool first
        var rawFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, cancellationToken);
        var questionsFromPool = rawFromPool
            .Where(q => !usedTexts.Contains(q.Text.Trim().ToLowerInvariant()))
            .ToList();
        _logger.LogInformation("Found {Count} non-duplicate pool questions for tour {Tour} topic '{Topic}' ({Raw} raw, {Dupes} dupes filtered)",
            questionsFromPool.Count, session.CurrentTour, topic, rawFromPool.Count, rawFromPool.Count - questionsFromPool.Count);

        if (questionsFromPool.Count >= requiredPerTour)
        {
            session.QuestionsByTour[session.CurrentTour] = new Queue<Question>(questionsFromPool.Take(requiredPerTour));
            _logger.LogInformation("Using {Count} pooled questions for tour {Tour}", requiredPerTour, session.CurrentTour);
            await _repository.SaveAsync(session, cancellationToken);
            return;
        }

        // Not enough in pool - generate via API
        var selectedTopic = topic;
        var topicDisplay = topic;

        _logger.LogInformation("Generating questions for tour {Tour} with topic '{Topic}'",
            session.CurrentTour, topicDisplay);

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

            var poolArchivedQuestions = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(selectedTopic, cancellationToken);

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

    /// <summary>
    /// Reads the "no questions" consecutive-completion counter from metadata, tolerating the
    /// int/long/JsonElement forms it can take after a session round-trips through JSON persistence.
    /// </summary>
    private static int GetNoQuestionsStreak(GameSession session)
    {
        if (!session.Metadata.TryGetValue("NoQuestionsStreak", out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt32(),
            _ => 0
        };
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

