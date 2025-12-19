using Microsoft.Extensions.Logging;
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

        // Check if we need to generate more questions
        await EnsureQuestionsAvailableAsync(session, cancellationToken);

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

                    // Check if ties are resolved among sudden death participants using SuddenDeathScore
                    var suddenDeathScores = participants.Select(p => p.SuddenDeathScore).ToList();
                    var hasConflicts = suddenDeathScores.Count != suddenDeathScores.Distinct().Count();

                    if (!hasConflicts)
                    {
                        _logger.LogInformation("Sudden death resolved. Ties broken among {Count} participants.", participants.Count);

                        var resolvedText = _localization.GetString(session.Language, "Game.SuddenDeathResolved");
                        await _messenger.SendAsync(session.ChatId, resolvedText, cancellationToken);

                        // Determine who to eliminate based on SuddenDeathScore
                        var lowestSuddenDeathScore = participants.Min(p => p.SuddenDeathScore);
                        var toEliminate = participants.Where(p => p.SuddenDeathScore == lowestSuddenDeathScore).ToList();

                        foreach (var player in toEliminate)
                        {
                            player.Status = PlayerStatus.Eliminated;
                            _logger.LogInformation("Player {PlayerName} eliminated after sudden death. SuddenDeathScore: {SuddenDeathScore}, MainScore: {Score}",
                                player.DisplayName, player.SuddenDeathScore, player.Score);
                            var elimText = string.Format(
                                _localization.GetString(session.Language, "Game.Eliminated"),
                                player.DisplayName);
                            await _messenger.SendAsync(session.ChatId, elimText, cancellationToken);
                        }

                        // Clear sudden death state and reset sudden death scores
                        foreach (var player in participants)
                        {
                            player.SuddenDeathScore = 0;
                        }
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

                                // Reset sudden death scores for new round
                                foreach (var player in tiedPlayers)
                                {
                                    player.SuddenDeathScore = 0;
                                }

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
                            // Note: AdvanceRoundAsync will announce the tour topic when CurrentRound becomes 1
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

                // Announce tour topic at the start of the first round
                if (session.CurrentRound == 1)
                {
                    var currentTopic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
                    var tourStartText = string.Format(
                        _localization.GetString(session.Language, "Game.TourStart"),
                        session.CurrentTour,
                        session.Tours,
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
            text = string.Format(
                _localization.GetString(session.Language, "Game.Round"),
                session.CurrentRound + 1,
                session.RoundsPerTour,
                currentPlayer.DisplayName,
                session.CurrentQuestion.Text,
                session.AnswerTimeoutSeconds);
        }

        var messageId = await _messenger.SendAsync(session.ChatId, text, cancellationToken);
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
            // In sudden death mode, only update SuddenDeathScore, not the main Score
            if (session.Status == GameStatus.SuddenDeath)
            {
                player.SuddenDeathScore += 1;
                _logger.LogInformation("Player {PlayerName} answered CORRECTLY in sudden death! SuddenDeathScore: {SuddenDeathScore}",
                    player.DisplayName, player.SuddenDeathScore);
            }
            else
            {
                player.Score += 1;
                _logger.LogInformation("Player {PlayerName} answered CORRECTLY! Score: {Score}", player.DisplayName, player.Score);
            }
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

        // In sudden death, check immediately if ties are resolved
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

                // Check if ties are resolved among sudden death participants
                var suddenDeathScores = participants.Select(p => p.SuddenDeathScore).ToList();
                var hasConflicts = suddenDeathScores.Count != suddenDeathScores.Distinct().Count();

                if (!hasConflicts && suddenDeathScores.Any(s => s > 0))
                {
                    // Ties resolved - at least one player has scored and all scores are different
                    _logger.LogInformation("Sudden death resolved immediately. Ties broken among {Count} participants.", participants.Count);

                    var resolvedText = _localization.GetString(session.Language, "Game.SuddenDeathResolved");
                    await _messenger.SendAsync(session.ChatId, resolvedText, cancellationToken);

                    // Determine who to eliminate based on SuddenDeathScore
                    var lowestSuddenDeathScore = participants.Min(p => p.SuddenDeathScore);
                    var toEliminate = participants.Where(p => p.SuddenDeathScore == lowestSuddenDeathScore).ToList();

                    foreach (var playerToEliminate in toEliminate)
                    {
                        playerToEliminate.Status = PlayerStatus.Eliminated;
                        _logger.LogInformation("Player {PlayerName} eliminated after sudden death. SuddenDeathScore: {SuddenDeathScore}, MainScore: {Score}",
                            playerToEliminate.DisplayName, playerToEliminate.SuddenDeathScore, playerToEliminate.Score);
                        var elimText = string.Format(
                            _localization.GetString(session.Language, "Game.Eliminated"),
                            playerToEliminate.DisplayName);
                        await _messenger.SendAsync(session.ChatId, elimText, cancellationToken);
                    }

                    // Clear sudden death state
                    foreach (var p in participants)
                    {
                        p.SuddenDeathScore = 0;
                    }
                    session.Metadata.Remove("SuddenDeathParticipants");
                    session.Status = GameStatus.InProgress;
                    session.TurnQueue.Clear();

                    await _repository.SaveAsync(session, cancellationToken);

                    // Check if game should end
                    var remaining = session.ActivePlayers.ToList();
                    if (remaining.Count <= 1 || session.CurrentTour > session.Tours)
                    {
                        await CompleteGameAsync(session, cancellationToken);
                        return;
                    }

                    // Continue with next tour or complete game
                    await CompleteTourAsync(session, cancellationToken);
                    return;
                }
            }
        }

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

                    // Reset sudden death scores for participants
                    foreach (var player in tiedForLowest)
                    {
                        player.SuddenDeathScore = 0;
                    }

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

                // Reset sudden death scores for participants
                foreach (var player in tiedPlayers)
                {
                    player.SuddenDeathScore = 0;
                }

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
        // Determine how many questions we need in reserve
        int threshold = session.Status == GameStatus.SuddenDeath ? 15 : 5;
        int targetBuffer = session.Status == GameStatus.SuddenDeath ? 20 : 10;

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

        _logger.LogInformation("Running low on questions for tour {Tour} (current: {Count}, threshold: {Threshold}). Generating more...",
            session.CurrentTour, questions.Count, threshold);

        try
        {
            var topic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
            var provider = _questionProviderFactory.Resolve(session.QuestionSourceMode);

            // Calculate how many questions to generate
            var questionsToGenerate = Math.Max(targetBuffer - questions.Count, targetBuffer);
            _logger.LogInformation("Generating {Count} new questions for topic '{Topic}'", questionsToGenerate, topic);

            // Get archived questions from both session and pool repository to avoid repetition
            var sessionAskedQuestions = session.Metadata.TryGetValue("AskedQuestions", out var askedObj)
                ? ExtractAskedQuestions(askedObj)
                : new List<Question>();

            var poolArchivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(cancellationToken);

            // Combine both sources
            var allArchivedQuestions = new List<Question>(sessionAskedQuestions);
            allArchivedQuestions.AddRange(poolArchivedQuestions);

            _logger.LogInformation("Using {SessionCount} session questions + {PoolCount} pool archived questions for AI context",
                sessionAskedQuestions.Count, poolArchivedQuestions.Count);

            // Generate questions
            IReadOnlyDictionary<int, List<Question>> generated;
            if (provider is AiQuestionProvider aiProvider)
            {
                generated = await aiProvider.PrepareQuestionPoolAsync(
                    new[] { topic },
                    1,
                    questionsToGenerate,
                    session.Players,
                    session.Language,
                    allArchivedQuestions,
                    cancellationToken);
            }
            else
            {
                generated = await provider.PrepareQuestionPoolAsync(
                    new[] { topic },
                    1,
                    questionsToGenerate,
                    session.Players,
                    session.Language,
                    cancellationToken);
            }

            var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
            _logger.LogInformation("Generated {Count} new questions. Adding to current tour queue.", generatedList.Count);

            // Get existing question texts in the queue to avoid duplicates
            var existingQuestionTexts = new HashSet<string>(
                questions.Select(q => q.Text.Trim().ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            // Add generated questions to the current tour, skipping duplicates
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

            _logger.LogInformation("Added {Added} unique questions, skipped {Skipped} duplicates", added, skipped);

            await _repository.SaveAsync(session, cancellationToken);

            var statusMessage = session.Language == GameLanguage.Russian
                ? $"🔄 Добавлено {added} новых вопросов"
                : $"🔄 Added {added} new questions";

            await _messenger.SendAsync(session.ChatId, statusMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate questions on the fly for chat {ChatId}, tour {Tour}",
                session.ChatId, session.CurrentTour);

            // Don't throw - let the game continue with whatever questions remain
            // The game will end gracefully if it truly runs out
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

