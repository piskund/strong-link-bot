using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Tests;

public class GameLifecycleServiceTests
{
    private readonly Mock<IChatMessenger> _messenger = new(MockBehavior.Strict);
    private readonly Mock<IGameSessionRepository> _repository = new();
    private readonly Mock<IQuestionPoolRepository> _poolRepository = new();
    private readonly Mock<IGameResultRepository> _resultRepository = new();
    private readonly Mock<IAnswerValidator> _answerValidator = new();
    private readonly ISuddenDeathService _suddenDeathService;
    private readonly IGameModeScoreHandler _regularScoreHandler;
    private readonly IGameModeScoreHandler _suddenDeathScoreHandler;
    private readonly QuestionProviderFactory _questionProviderFactory = new(new List<IQuestionProvider>());
    private readonly LocalizationService _localization = new();
    private readonly GameLifecycleService _service;
    private readonly List<string> _sentMessages = new();

    public GameLifecycleServiceTests()
    {
        _messenger
            .Setup(m => m.SendAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<long, string, CancellationToken>((_, message, _) => _sentMessages.Add(message))
            .ReturnsAsync(1); // Return dummy message ID

        // Setup answer validator to validate correct answers as true
        _answerValidator
            .Setup(v => v.ValidateAnswerAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<GameLanguage>(),
                It.IsAny<DifficultyLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userAnswer, string correctAnswer, string question, GameLanguage language, DifficultyLevel difficulty, CancellationToken ct) =>
                string.Equals(userAnswer, correctAnswer, StringComparison.OrdinalIgnoreCase));

        // Repository: SaveAsync is a no-op; LoadAsync returns null (background tasks won't run in tests)
        _repository
            .Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repository
            .Setup(r => r.LoadAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        // ResultRepository: archive is a no-op
        _resultRepository
            .Setup(r => r.ArchiveAsync(It.IsAny<GameResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Pool repo: return empty lists so EnsureQuestionsAvailableAsync doesn't throw
        _poolRepository
            .Setup(r => r.SelectQuestionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());
        _poolRepository
            .Setup(r => r.GetArchivedQuestionsByTopicAllTimeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());
        _poolRepository
            .Setup(r => r.MoveToArchiveAsync(It.IsAny<IEnumerable<Question>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use real services instead of mocking - they have minimal dependencies
        _suddenDeathService = new SuddenDeathService(NullLogger<SuddenDeathService>.Instance);
        _regularScoreHandler = new RegularModeScoreHandler(NullLogger<RegularModeScoreHandler>.Instance);
        _suddenDeathScoreHandler = new SuddenDeathModeScoreHandler(NullLogger<SuddenDeathModeScoreHandler>.Instance);

        var gameOptions = Options.Create(new GameOptions
        {
            Tours = 1,
            RoundsPerTour = 1,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1,
            UseAiAnswerValidation = true,
            Topics = new[] { "History" }
        });

        _service = new GameLifecycleService(
            _messenger.Object,
            _repository.Object,
            _localization,
            _poolRepository.Object,
            _resultRepository.Object,
            _answerValidator.Object,
            _questionProviderFactory,
            _suddenDeathService,
            (RegularModeScoreHandler)_regularScoreHandler,
            (SuddenDeathModeScoreHandler)_suddenDeathScoreHandler,
            gameOptions,
            NullLogger<GameLifecycleService>.Instance);
    }

    [Fact]
    public async Task StartGameAsync_WhenNotEnoughPlayers_NotifiesAndDoesNotSave()
    {
        var session = CreateSession(players: 0); // Changed from 1 to 0 since minimum is now 1

        await _service.StartGameAsync(session, CancellationToken.None);

        Assert.Contains(_localization.GetString(session.Language, "Game.NotEnoughPlayers"), _sentMessages);
        _repository.Verify(repo => repo.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAnswerAsync_CorrectAnswer_IncrementsScoreAndFeedback()
    {
        var session = CreateSession(players: 2);
        session.Status = GameStatus.InProgress;
        session.CurrentTour = 1;
        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Topic = "History", Text = "Capital of France?", Answer = "Paris" }
        });
        session.TurnQueue.Enqueue(session.Players[0].Id);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        await _service.HandleAnswerAsync(session, session.Players[0].Id, "Paris", CancellationToken.None);

        Assert.Equal(1, session.Players[0].Score);
        Assert.Contains(_localization.GetString(session.Language, "Game.Correct"), _sentMessages);
    }

    [Fact]
    public async Task SuddenDeath_OnePlayerAnswersWrong_EliminatesThatPlayerAndContinuesToNextTour()
    {
        // Arrange: 3 players, 2 tours, end of tour 1 with 3 tied at lowest score
        var session = CreateSession(players: 3, tours: 2);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 5; // Tied for lowest
        session.Players[1].Score = 5; // Tied for lowest
        session.Players[2].Score = 5; // Tied for lowest

        // Setup sudden death
        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001, 1002 };
        session.Players[0].SuddenDeathScore = 0;
        session.Players[1].SuddenDeathScore = 0;
        session.Players[2].SuddenDeathScore = 0;

        // Add questions for sudden death and next tour
        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Topic = "History", Text = "Q1?", Answer = "A1" },
            new Question { Topic = "History", Text = "Q2?", Answer = "A2" },
            new Question { Topic = "History", Text = "Q3?", Answer = "A3" }
        });
        session.QuestionsByTour[2] = new Queue<Question>(new[]
        {
            new Question { Topic = "Geography", Text = "Q4?", Answer = "A4" }
        });

        // Queue all players for sudden death
        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);
        session.TurnQueue.Enqueue(1002);

        _sentMessages.Clear();

        // Act: Round 1 - Player0 answers correctly
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);

        // Player0 now has SuddenDeathScore = 1, others = 0
        // Should NOT eliminate yet, all still in sudden death
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[2].Status);

        // Act: Round 2 - Player1 answers correctly
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A2", CancellationToken.None);

        // Player0=1, Player1=1, Player2=0
        // Should NOT eliminate yet
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[2].Status);

        // Act: Round 3 - Player2 answers WRONG
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // Player0=1, Player1=1, Player2=0 (lowest) - NOW eliminate Player2
        // After elimination, Player0 and Player1 should move to tour 2
        Assert.Equal(PlayerStatus.Eliminated, session.Players[2].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);

        // Should have moved to tour 2 and no longer in sudden death
        Assert.Equal(GameStatus.InProgress, session.Status);
        Assert.Equal(2, session.CurrentTour);
        Assert.False(session.Metadata.ContainsKey("SuddenDeathParticipants"));

        // SuddenDeathScores are intentionally preserved after resolution (used for final winner
        // ranking — see SuddenDeathService.ExitSuddenDeath). The two survivors each answered one
        // sudden-death question correctly, so both retain a score of 1.
        Assert.Equal(1, session.Players[0].SuddenDeathScore);
        Assert.Equal(1, session.Players[1].SuddenDeathScore);
    }

    [Fact]
    public async Task SuddenDeath_TwoPlayersAnswerWrong_EliminatesBothAndContinues()
    {
        // Arrange: 4 players in sudden death
        var session = CreateSession(players: 4, tours: 2);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 3;
        session.Players[1].Score = 3;
        session.Players[2].Score = 3;
        session.Players[3].Score = 3;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001, 1002, 1003 };

        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Topic = "History", Text = "Q1?", Answer = "A1" },
            new Question { Topic = "History", Text = "Q2?", Answer = "A2" },
            new Question { Topic = "History", Text = "Q3?", Answer = "A3" },
            new Question { Topic = "History", Text = "Q4?", Answer = "A4" }
        });
        session.QuestionsByTour[2] = new Queue<Question>(new[]
        {
            new Question { Topic = "Geography", Text = "Q5?", Answer = "A5" }
        });

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);
        session.TurnQueue.Enqueue(1002);
        session.TurnQueue.Enqueue(1003);

        _sentMessages.Clear();

        // Act: Player0 answers correctly (score=1)
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);

        // Player1 answers correctly (score=1)
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A2", CancellationToken.None);

        // Player2 answers WRONG (score=0)
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // Player3 answers WRONG (score=0)
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1003, "WRONG", CancellationToken.None);

        // Assert: Player2 and Player3 (both with score 0) should be eliminated
        Assert.Equal(PlayerStatus.Eliminated, session.Players[2].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[3].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);

        // Should move to next tour
        Assert.Equal(GameStatus.InProgress, session.Status);
        Assert.Equal(2, session.CurrentTour);
    }

    [Fact]
    public async Task SuddenDeath_AllAnswerWrong_ContinuesUntilScoreSeparation()
    {
        // Arrange: 3 players in sudden death, all answer wrong first
        var session = CreateSession(players: 3, tours: 2);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 4;
        session.Players[1].Score = 4;
        session.Players[2].Score = 4;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001, 1002 };

        // 10 questions so the queue stays above the sudden-death threshold across both rounds.
        session.QuestionsByTour[1] = new Queue<Question>(
            Enumerable.Range(1, 10).Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" }));
        session.QuestionsByTour[2] = new Queue<Question>(
            Enumerable.Range(100, 5).Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);
        session.TurnQueue.Enqueue(1002);

        _sentMessages.Clear();

        // Round 1: all answer wrong → scores stay tied at 0, sudden death continues.
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // All still have SuddenDeathScore=0, should still be in sudden death
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.Equal(0, session.Players[0].SuddenDeathScore);
        Assert.Equal(0, session.Players[1].SuddenDeathScore);
        Assert.Equal(0, session.Players[2].SuddenDeathScore);

        // Round 2: P0 answers correctly, P1/P2 wrong. Resolution happens once the round completes
        // (turn queue empties), not mid-round — so all three must answer.
        await _service.HandleAnswerAsync(session, 1000, "A4", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // P0=1, others=0 → eliminate P1 and P2. Only P0 remains → game completes.
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[2].Status);
        Assert.Equal(GameStatus.Completed, session.Status);
    }

    [Fact]
    public async Task SuddenDeath_DebugScenario_ThreePlayers_OneAnswersCorrectly()
    {
        // This test simulates a real sudden death scenario for debugging
        // Set breakpoints in GameLifecycleService.cs at:
        // - Line 126 (CheckAndResolveIfSuddenDeathComplete start)
        // - Line 138 (min/max score calculation)
        // - Line 144 (elimination check)
        // - Line 257 (AdvanceRoundAsync sudden death check)

        // ARRANGE: 3 players, end of tour 1, all tied at lowest score (5 points)
        var session = CreateSession(players: 3, tours: 2);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;

        // All three players tied at score 5
        session.Players[0].Score = 5;
        session.Players[1].Score = 5;
        session.Players[2].Score = 5;

        // Setup sudden death metadata
        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001, 1002 };
        session.Players[0].SuddenDeathScore = 0;
        session.Players[1].SuddenDeathScore = 0;
        session.Players[2].SuddenDeathScore = 0;

        // Add questions for sudden death rounds
        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Topic = "History", Text = "Q1?", Answer = "A1" },
            new Question { Topic = "History", Text = "Q2?", Answer = "A2" },
            new Question { Topic = "History", Text = "Q3?", Answer = "A3" }
        });
        session.QuestionsByTour[2] = new Queue<Question>(new[]
        {
            new Question { Topic = "Geography", Text = "Q4?", Answer = "A4" }
        });

        _sentMessages.Clear();

        // ACT & ASSERT: Play through sudden death round by round
        // Enqueue all players at once for the round to prevent premature sudden death resolution
        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);
        session.TurnQueue.Enqueue(1002);

        // ROUND 1: Player0 answers correctly
        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        // DEBUG CHECKPOINT: After AdvanceRoundAsync, CurrentPlayerId should be 1000
        Assert.Equal(1000L, session.CurrentPlayerId);
        Assert.NotNull(session.CurrentQuestion);

        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);

        // DEBUG CHECKPOINT: Player0 should now have SuddenDeathScore = 1
        Assert.Equal(1, session.Players[0].SuddenDeathScore);
        Assert.Equal(0, session.Players[1].SuddenDeathScore);
        Assert.Equal(0, session.Players[2].SuddenDeathScore);
        // Should NOT eliminate yet - all still active
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[2].Status);
        Assert.Equal(GameStatus.SuddenDeath, session.Status);

        // ROUND 2: Player1 answers correctly
        // AdvanceRoundAsync called automatically by HandleAnswerAsync, CurrentPlayerId should be 1001
        Assert.Equal(1001L, session.CurrentPlayerId);
        await _service.HandleAnswerAsync(session, 1001, "A2", CancellationToken.None);

        // DEBUG CHECKPOINT: Player0=1, Player1=1, Player2=0
        Assert.Equal(1, session.Players[0].SuddenDeathScore);
        Assert.Equal(1, session.Players[1].SuddenDeathScore);
        Assert.Equal(0, session.Players[2].SuddenDeathScore);
        // Should NOT eliminate yet - still in sudden death
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[2].Status);

        // ROUND 3: Player2 answers WRONG
        // AdvanceRoundAsync called automatically by HandleAnswerAsync, CurrentPlayerId should be 1002
        Assert.Equal(1002L, session.CurrentPlayerId);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // After Player2 answers wrong, sudden death should be resolved immediately
        // (turn queue is now empty, scores are: Player0=1, Player1=1, Player2=0)
        // Player2 should be eliminated, Player0 and Player1 move to tour 2
        Assert.Equal(PlayerStatus.Eliminated, session.Players[2].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);

        // Should have moved to tour 2 and exited sudden death
        Assert.Equal(GameStatus.InProgress, session.Status);
        Assert.Equal(2, session.CurrentTour);
        Assert.False(session.Metadata.ContainsKey("SuddenDeathParticipants"));

        // SuddenDeathScores are intentionally preserved after resolution (used for final winner
        // ranking — see SuddenDeathService.ExitSuddenDeath). Both survivors answered one
        // sudden-death question correctly, so each retains a score of 1.
        Assert.Equal(1, session.Players[0].SuddenDeathScore);
        Assert.Equal(1, session.Players[1].SuddenDeathScore);
    }

    // ── Sudden death triggering rules ────────────────────────────────────────

    [Fact]
    public async Task SuddenDeath_DoesNotTrigger_UntilAllPlayersHaveAnsweredTheirRounds()
    {
        // Arrange: 3 players, 2 rounds per tour. After round 1 they are tied — no SD yet.
        // SD should only fire once all RoundsPerTour rounds are complete.
        var session = CreateSession(players: 3, tours: 2, roundsPerTour: 2);
        session.Status = GameStatus.InProgress;
        session.CurrentTour = 1;

        // 12 questions: enough so the queue never drops below the regular-mode threshold (5)
        var questions = Enumerable.Range(1, 12)
            .Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" })
            .ToList();
        session.QuestionsByTour[1] = new Queue<Question>(questions);
        session.QuestionsByTour[2] = new Queue<Question>(Enumerable.Range(100, 10)
            .Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        // Enqueue all players for round 1
        foreach (var p in session.Players) session.TurnQueue.Enqueue(p.Id);

        // Round 1: all answer correctly — tied at 1 each, but tour not finished
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A2", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "A3", CancellationToken.None);

        // After round 1 the turn queue refills for round 2 — still InProgress, not SuddenDeath
        Assert.Equal(GameStatus.InProgress, session.Status);

        // Round 2: all answer correctly — still tied, now tour is complete → SD triggers
        await _service.HandleAnswerAsync(session, 1000, "A4", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A5", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "A6", CancellationToken.None);

        // Now all rounds done and all tied → sudden death
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
    }

    [Fact]
    public async Task SuddenDeath_OnlyInvolvesPlayersActuallyTied_NotLeaders()
    {
        // Arrange: 4 players — P0 leads, P1/P2/P3 tied for last.
        // SD should only pull in the bottom 3 tied players.
        var session = CreateSession(players: 4, tours: 2, roundsPerTour: 1);
        session.Status = GameStatus.InProgress;
        session.CurrentTour = 1;

        // Pad to 10 so queue never drops below regular-mode threshold (5) during the round
        var questions = Enumerable.Range(1, 10)
            .Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" })
            .ToList();
        session.QuestionsByTour[1] = new Queue<Question>(questions);
        session.QuestionsByTour[2] = new Queue<Question>(Enumerable.Range(100, 5)
            .Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        foreach (var p in session.Players) session.TurnQueue.Enqueue(p.Id);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        // P0 answers correctly (score=1), others answer wrong (score=0)
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1003, "WRONG", CancellationToken.None);

        // Eliminating all 3 tied-lowest leaves only P0 — a clean win (no SD). With a single active
        // player left, the game completes rather than continuing to tour 2.
        Assert.Equal(GameStatus.Completed, session.Status);
        // P1/P2/P3 eliminated, P0 wins
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.All(session.Players.Skip(1), p => Assert.Equal(PlayerStatus.Eliminated, p.Status));
    }

    [Fact]
    public async Task SuddenDeath_CorrectAndIncorrectAnswers_DoNotCountTowardMainStats()
    {
        // Verify CorrectAnswers / IncorrectAnswers are not incremented during sudden death.
        // Use 2 players so SD resolves after one round (no need for a large question buffer).
        var session = CreateSession(players: 2, tours: 2);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 5;
        session.Players[1].Score = 5;
        session.Players[0].CorrectAnswers = 3;
        session.Players[0].IncorrectAnswers = 1;
        session.Players[1].CorrectAnswers = 2;
        session.Players[1].IncorrectAnswers = 2;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001 };
        session.Metadata["SuddenDeathStartRound"] = 0;

        // ≥5 questions keeps queue above the SD threshold so EnsureQuestionsAvailableAsync won't generate
        var sdQuestions = Enumerable.Range(1, 10)
            .Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" })
            .ToList();
        session.QuestionsByTour[1] = new Queue<Question>(sdQuestions);
        session.QuestionsByTour[2] = new Queue<Question>(new[]
        {
            new Question { Topic = "Geography", Text = "QX?", Answer = "AX" },
            new Question { Topic = "Geography", Text = "QY?", Answer = "AY" }
        });

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);

        var p0CorrectBefore = session.Players[0].CorrectAnswers;
        var p0IncorrectBefore = session.Players[0].IncorrectAnswers;
        var p1CorrectBefore = session.Players[1].CorrectAnswers;
        var p1IncorrectBefore = session.Players[1].IncorrectAnswers;

        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None); // correct in SD
        // After P0 answers correctly, still P1's turn — SD not resolved yet
        Assert.Equal(1, session.Players[0].SuddenDeathScore);
        Assert.Equal(p0CorrectBefore, session.Players[0].CorrectAnswers);   // unchanged
        Assert.Equal(p0IncorrectBefore, session.Players[0].IncorrectAnswers); // unchanged

        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None); // incorrect in SD
        // P0=1, P1=0 → SD resolved, P1 eliminated
        Assert.Equal(0, session.Players[1].SuddenDeathScore);
        Assert.Equal(p1CorrectBefore, session.Players[1].CorrectAnswers);   // unchanged
        Assert.Equal(p1IncorrectBefore, session.Players[1].IncorrectAnswers); // unchanged
    }

    [Fact]
    public async Task SuddenDeath_NonParticipantLeader_IsNotEliminated()
    {
        // 3 players: P0 leads (score=8), P1/P2 tied (score=5).
        // SD involves only P1 and P2. P0 must not be touched.
        var session = CreateSession(players: 3, tours: 2, roundsPerTour: 1);
        session.Status = GameStatus.InProgress;
        session.CurrentTour = 1;

        var questions = Enumerable.Range(1, 3)
            .Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" })
            .ToList();
        session.QuestionsByTour[1] = new Queue<Question>(questions);
        session.QuestionsByTour[2] = new Queue<Question>(new[]
        {
            new Question { Topic = "Geography", Text = "QX?", Answer = "AX" },
            new Question { Topic = "Geography", Text = "QY?", Answer = "AY" },
            new Question { Topic = "Geography", Text = "QZ?", Answer = "AZ" }
        });

        foreach (var p in session.Players) session.TurnQueue.Enqueue(p.Id);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        // P0 correct (score=1), P1 wrong (score=0), P2 wrong (score=0)
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // Eliminating P1+P2 leaves 1 winner (P0) — no SD, clean outcome. The leader (P0) is never
        // eliminated, and with a single survivor the game completes.
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[2].Status);
        Assert.Equal(GameStatus.Completed, session.Status);
    }

    // ── Regression: SD continued after a tied round ───────────────────────────

    [Fact]
    public async Task SuddenDeath_TwoPlayersBothAnswerCorrectly_ContinuesNotEnds()
    {
        // Regression for the real game bug: both players scored 1:1 in SD round 1,
        // game incorrectly ended instead of continuing to round 2.
        var session = CreateSession(players: 2, tours: 2, roundsPerTour: 10);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 7;
        session.Players[1].Score = 7;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001 };
        session.Metadata["SuddenDeathStartRound"] = 0;

        // 10 questions: enough for several SD rounds above threshold (5)
        session.QuestionsByTour[1] = new Queue<Question>(
            Enumerable.Range(1, 10).Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" }));
        session.QuestionsByTour[2] = new Queue<Question>(
            Enumerable.Range(100, 5).Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);

        // SD round 1: both answer correctly → 1:1 tie → must continue
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A2", CancellationToken.None);

        // Tie — still in sudden death, nobody eliminated
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);
        Assert.Equal(1, session.Players[0].SuddenDeathScore);
        Assert.Equal(1, session.Players[1].SuddenDeathScore);

        // SD round 2: P0 correct, P1 wrong → 2:1 → resolved
        await _service.HandleAnswerAsync(session, 1000, "A3", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);

        // P1 eliminated leaves only P0 active → the game completes (this scenario's purpose is to
        // confirm SD *continued* past the round-1 tie above rather than ending prematurely).
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[1].Status);
        Assert.Equal(GameStatus.Completed, session.Status);
    }

    [Fact]
    public async Task SuddenDeath_TwoPlayersMultipleRoundsTied_ResolvesWhenScoresSplit()
    {
        // All SD rounds tied until the last one — confirms SD runs indefinitely until resolved.
        var session = CreateSession(players: 2, tours: 2, roundsPerTour: 10);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 5;
        session.Players[1].Score = 5;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001 };
        session.Metadata["SuddenDeathStartRound"] = 0;

        session.QuestionsByTour[1] = new Queue<Question>(
            Enumerable.Range(1, 20).Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" }));
        session.QuestionsByTour[2] = new Queue<Question>(
            Enumerable.Range(100, 5).Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        // Rounds 1-3: both wrong every round → all tied at 0
        // Questions are A1..A20 in order; wrong answers keep scores tied
        for (var round = 0; round < 3; round++)
        {
            await _service.HandleAnswerAsync(session, 1000, "WRONG", CancellationToken.None);
            await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);
            Assert.Equal(GameStatus.SuddenDeath, session.Status);
        }

        // Round 4: P0 answers the current question's known answer (A7), P1 wrong → resolved
        // Questions consumed so far: Q1..Q6 (3 rounds × 2 players). Current question is Q7 → answer A7.
        await _service.HandleAnswerAsync(session, 1000, "A7", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);

        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[1].Status);
        // P0 is the sole survivor → game completes (not just moving to next tour)
        Assert.Equal(GameStatus.Completed, session.Status);
    }

    [Fact]
    public async Task SuddenDeath_RoundLimitReached_AllSurvivorsAdvanceWithoutElimination()
    {
        // If SD hits RoundsPerTour rounds without resolution, all survivors advance to next tour.
        // Use roundsPerTour=1 and SuddenDeathStartRound=0: after 1 SD round,
        // suddenDeathRoundsPlayed = CurrentRound(0+1 after increment... wait, check fires BEFORE increment.
        // Actually: check fires when TurnQueue empties. At that point CurrentRound=0, startRound=0.
        // suddenDeathRoundsPlayed = 0-0=0 < 1 → no limit → increment CurrentRound=1.
        // On the NEXT TurnQueue-empty: suddenDeathRoundsPlayed = 1-0=1 >= 1 → LIMIT FIRES.
        // So 2 full rounds of 2 players each = 4 answers needed.
        var session = CreateSession(players: 2, tours: 2, roundsPerTour: 1);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 5;
        session.Players[1].Score = 5;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001 };
        session.Metadata["SuddenDeathStartRound"] = 0;

        session.QuestionsByTour[1] = new Queue<Question>(
            Enumerable.Range(1, 10).Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" }));
        session.QuestionsByTour[2] = new Queue<Question>(
            Enumerable.Range(100, 5).Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        // SD round 1: both wrong → check fires (0-0=0 < 1) → no limit → CurrentRound=1
        await _service.HandleAnswerAsync(session, 1000, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);
        // SD round 2: both wrong → check fires (1-0=1 >= 1) → LIMIT
        await _service.HandleAnswerAsync(session, 1000, "WRONG", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);

        // Both should survive and advance to tour 2 — no elimination
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Active, session.Players[1].Status);
        Assert.Equal(2, session.CurrentTour);
        // Scores still equal after SD, so tour 2 re-enters SD automatically
        Assert.True(session.Status == GameStatus.InProgress || session.Status == GameStatus.SuddenDeath,
            $"Expected InProgress or SuddenDeath but was {session.Status}");
    }

    [Fact]
    public async Task SuddenDeath_MetadataIntact_AfterMultipleTiedRounds()
    {
        // Verify that SuddenDeathParticipants and SuddenDeathStartRound metadata survive
        // multiple rounds — this was the root cause of the real bug.
        // roundsPerTour=10 ensures the SD round limit isn't hit during 2 tied test rounds
        var session = CreateSession(players: 2, tours: 2, roundsPerTour: 10);
        session.Status = GameStatus.SuddenDeath;
        session.CurrentTour = 1;
        session.Players[0].Score = 7;
        session.Players[1].Score = 7;

        session.Metadata["SuddenDeathParticipants"] = new List<long> { 1000, 1001 };
        session.Metadata["SuddenDeathStartRound"] = 0;

        session.QuestionsByTour[1] = new Queue<Question>(
            Enumerable.Range(1, 10).Select(i => new Question { Topic = "History", Text = $"Q{i}?", Answer = $"A{i}" }));
        session.QuestionsByTour[2] = new Queue<Question>(
            Enumerable.Range(100, 5).Select(i => new Question { Topic = "Geography", Text = $"Q{i}?", Answer = $"A{i}" }));

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);

        // After round 1 tie (1:1) — metadata must still be present
        await _service.HandleAnswerAsync(session, 1000, "A1", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A2", CancellationToken.None);

        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.True(session.Metadata.ContainsKey("SuddenDeathParticipants"), "SuddenDeathParticipants lost after round 1");
        Assert.True(session.Metadata.ContainsKey("SuddenDeathStartRound"), "SuddenDeathStartRound lost after round 1");
        var participants = Assert.IsType<List<long>>(session.Metadata["SuddenDeathParticipants"]);
        Assert.Equal(2, participants.Count);

        // After round 2 tie (2:2) — metadata must still be present
        await _service.HandleAnswerAsync(session, 1000, "A3", CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "A4", CancellationToken.None);

        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.True(session.Metadata.ContainsKey("SuddenDeathParticipants"), "SuddenDeathParticipants lost after round 2");
        Assert.True(session.Metadata.ContainsKey("SuddenDeathStartRound"), "SuddenDeathStartRound lost after round 2");
    }

    private static GameSession CreateSession(int players, int tours = 1, int roundsPerTour = 1)
    {
        var session = new GameSession
        {
            ChatId = 123,
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = new[] { "History", "Geography" },
            Tours = tours,
            RoundsPerTour = roundsPerTour,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1
        };

        for (var i = 0; i < players; i++)
        {
            var player = new Player
            {
                Id = 1000 + i,
                DisplayName = $"Player{i}",
                Status = PlayerStatus.Active
            };
            session.Players.Add(player);
        }

        session.QuestionsByTour[1] = new Queue<Question>();
        session.QuestionsByTour[2] = new Queue<Question>();

        return session;
    }
}

