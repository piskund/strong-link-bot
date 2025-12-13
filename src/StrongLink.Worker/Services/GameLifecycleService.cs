using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;

namespace StrongLink.Worker.Services;

public sealed class GameLifecycleService : IGameLifecycleService
{
    private readonly IChatMessenger _messenger;
    private readonly IGameSessionRepository _repository;
    private readonly ILocalizationService _localization;
    private readonly IQuestionPoolRepository _poolRepository;
    private readonly IGameResultRepository _resultRepository;
    private readonly IAnswerValidator _answerValidator;
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
        IOptions<GameOptions> gameOptions,
        ILogger<GameLifecycleService> logger)
    {
        _messenger = messenger;
        _repository = repository;
        _localization = localization;
        _poolRepository = poolRepository;
        _resultRepository = resultRepository;
        _answerValidator = answerValidator;
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

        if (!session.QuestionsByTour.TryGetValue(session.CurrentTour, out var questions) || questions.Count == 0)
        {
            _logger.LogInformation("No questions remaining for tour {Tour}. Completing tour.", session.CurrentTour);
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        if (session.TurnQueue.Count == 0)
        {
            // In sudden death mode, check if ties are resolved after each round
            if (session.Status == GameStatus.SuddenDeath)
            {
                if (session.Metadata.TryGetValue("SuddenDeathParticipants", out var participantsObj) &&
                    participantsObj is List<long> participantIds)
                {
                    var participants = participantIds
                        .Select(id => session.FindPlayer(id))
                        .Where(p => p != null && p.Status == PlayerStatus.Active)
                        .Cast<Player>()
                        .ToList();

                    _logger.LogDebug("Checking sudden death progress. Participants: {Count}", participants.Count);

                    // Check if ties are resolved among sudden death participants
                    var scores = participants.Select(p => p.Score).ToList();
                    var hasConflicts = scores.Count != scores.Distinct().Count();

                    if (!hasConflicts)
                    {
                        _logger.LogInformation("Sudden death resolved. Ties broken among {Count} participants.", participants.Count);

                        var resolvedText = _localization.GetString(session.Language, "Game.SuddenDeathResolved");
                        await _messenger.SendAsync(session.ChatId, resolvedText, cancellationToken);

                        // Determine who to eliminate based on original context
                        // If we had 4+ players going to <3, eliminate all but highest scorer in sudden death
                        var lowestScore = participants.Min(p => p.Score);
                        var toEliminate = participants.Where(p => p.Score == lowestScore).ToList();

                        foreach (var player in toEliminate)
                        {
                            player.Status = PlayerStatus.Eliminated;
                            _logger.LogInformation("Player {PlayerName} eliminated after sudden death. Score: {Score}",
                                player.DisplayName, player.Score);
                            var elimText = string.Format(
                                _localization.GetString(session.Language, "Game.Eliminated"),
                                player.DisplayName);
                            await _messenger.SendAsync(session.ChatId, elimText, cancellationToken);
                        }

                        // Clear sudden death state
                        session.Metadata.Remove("SuddenDeathParticipants");
                        session.Status = GameStatus.InProgress;

                        // Check if game should end now
                        var remaining = session.ActivePlayers.ToList();
                        if (remaining.Count <= 3)
                        {
                            // Check for more ties
                            var tiedGroups = remaining.GroupBy(p => p.Score).Where(g => g.Count() > 1).ToList();
                            if (tiedGroups.Any())
                            {
                                // More ties - another sudden death
                                _logger.LogInformation("More ties detected after sudden death resolution. Starting another sudden death.");
                                var tiedPlayers = tiedGroups.SelectMany(g => g).ToList();

                                session.Status = GameStatus.SuddenDeath;
                                session.Metadata["SuddenDeathParticipants"] = tiedPlayers.Select(p => p.Id).ToList();

                                var sdText = _localization.GetString(session.Language, "Game.SuddenDeath");
                                await _messenger.SendAsync(session.ChatId, sdText, cancellationToken);

                                foreach (var player in tiedPlayers)
                                {
                                    session.TurnQueue.Enqueue(player.Id);
                                }

                                session.CurrentRound = 0;
                                await _repository.SaveAsync(session, cancellationToken);
                                await AdvanceRoundAsync(session, cancellationToken);
                                return;
                            }
                            else
                            {
                                // No more ties - game over
                                await CompleteGameAsync(session, cancellationToken);
                                return;
                            }
                        }
                        else
                        {
                            // Continue to next tour
                            session.CurrentTour += 1;
                            if (session.CurrentTour > session.Tours)
                            {
                                await CompleteGameAsync(session, cancellationToken);
                                return;
                            }

                            // Start next tour
                            session.CurrentRound = 0;
                            foreach (var player in session.ActivePlayers)
                            {
                                session.TurnQueue.Enqueue(player.Id);
                            }

                            var nextTopic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
                            var textTour = string.Format(
                                _localization.GetString(session.Language, "Game.TourComplete"),
                                session.CurrentTour - 1,
                                nextTopic);
                            await _messenger.SendAsync(session.ChatId, textTour, cancellationToken);

                            await _repository.SaveAsync(session, cancellationToken);
                            await AdvanceRoundAsync(session, cancellationToken);
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Ties still present in sudden death. Continuing.");

                        // Queue only sudden death participants for next round
                        foreach (var player in participants)
                        {
                            session.TurnQueue.Enqueue(player.Id);
                        }
                    }
                }

                session.CurrentRound += 1;
                _logger.LogInformation("Starting sudden death round {Round}", session.CurrentRound + 1);
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
            text = string.Format(
                _localization.GetString(session.Language, "Game.Round"),
                session.CurrentRound + 1,
                session.RoundsPerTour,
                currentPlayer.DisplayName,
                session.CurrentQuestion.Text,
                session.AnswerTimeoutSeconds);
        }

        await _messenger.SendAsync(session.ChatId, text, cancellationToken);

        // Start answer timeout timer
        StartAnswerTimer(session.ChatId, session.CurrentQuestionAskedAt.Value, session.AnswerTimeoutSeconds);
    }

    public async Task HandleAnswerAsync(GameSession session, long playerId, string answer, CancellationToken cancellationToken)
    {
        _logger.LogDebug("HandleAnswerAsync: Player {PlayerId} answered: {Answer}", playerId, answer);

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
            player.Score += 1;
            player.CorrectAnswers += 1;
            _logger.LogInformation("Player {PlayerName} answered CORRECTLY! Score: {Score}", player.DisplayName, player.Score);
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
        await _repository.SaveAsync(session, cancellationToken);

        await AdvanceRoundAsync(session, cancellationToken);
    }

    public async Task StopGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping game for chat {ChatId}", session.ChatId);

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

        var activePlayers = session.ActivePlayers.ToList();
        if (activePlayers.Count > 1)
        {
            var minScore = activePlayers.Min(p => p.Score);
            var tiedForLowest = activePlayers
                .Where(p => p.Score == minScore)
                .ToList();

            var remainingAfterElimination = activePlayers.Count - tiedForLowest.Count;

            _logger.LogInformation("Tied for lowest score ({MinScore}): {Count} player(s). Would leave: {Remaining}",
                minScore, tiedForLowest.Count, remainingAfterElimination);

            if (remainingAfterElimination >= 3)
            {
                // Safe to eliminate all tied for lowest
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
            else if (remainingAfterElimination >= 1)
            {
                // Would leave 1-2 players, need to resolve rankings
                _logger.LogInformation("Elimination would leave {Remaining} players. Checking for sudden death need.",
                    remainingAfterElimination);

                if (tiedForLowest.Count > 1)
                {
                    // Multiple players tied for lowest - need sudden death to determine final rankings
                    _logger.LogInformation("Entering sudden death for {Count} players tied for lowest score",
                        tiedForLowest.Count);

                    session.Status = GameStatus.SuddenDeath;

                    // Track which players are in sudden death
                    session.Metadata["SuddenDeathParticipants"] = tiedForLowest.Select(p => p.Id).ToList();

                    var text = _localization.GetString(session.Language, "Game.SuddenDeath");
                    await _messenger.SendAsync(session.ChatId, text, cancellationToken);

                    // Queue only sudden death participants
                    foreach (var player in tiedForLowest)
                    {
                        session.TurnQueue.Enqueue(player.Id);
                    }

                    await _repository.SaveAsync(session, cancellationToken);
                    await AdvanceRoundAsync(session, cancellationToken);
                    return;
                }
                else
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

        // Check if we now have 3 or fewer active players with ties
        activePlayers = session.ActivePlayers.ToList();
        if (activePlayers.Count <= 3 && activePlayers.Count > 1)
        {
            var tiedGroups = activePlayers.GroupBy(p => p.Score).Where(g => g.Count() > 1).ToList();
            if (tiedGroups.Any())
            {
                // Have ties among final 3 or fewer - need sudden death
                var tiedPlayers = tiedGroups.SelectMany(g => g).ToList();
                _logger.LogInformation("Final {Count} players have ties. Entering sudden death for {TiedCount} tied players.",
                    activePlayers.Count, tiedPlayers.Count);

                session.Status = GameStatus.SuddenDeath;
                session.Metadata["SuddenDeathParticipants"] = tiedPlayers.Select(p => p.Id).ToList();

                var text = _localization.GetString(session.Language, "Game.SuddenDeath");
                await _messenger.SendAsync(session.ChatId, text, cancellationToken);

                // Queue only tied players
                foreach (var player in tiedPlayers)
                {
                    session.TurnQueue.Enqueue(player.Id);
                }

                await _repository.SaveAsync(session, cancellationToken);
                await AdvanceRoundAsync(session, cancellationToken);
                return;
            }
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
            await _repository.SaveAsync(session, CancellationToken.None);

            await AdvanceRoundAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling answer timeout for chat {ChatId}", chatId);
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

        var winner = session.ActivePlayers.OrderByDescending(p => p.Score).FirstOrDefault();
        if (winner != null)
        {
            _logger.LogInformation("Game winner: {PlayerName} with score {Score}", winner.DisplayName, winner.Score);
        }
        else
        {
            _logger.LogWarning("Game completed with no winner");
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
}

