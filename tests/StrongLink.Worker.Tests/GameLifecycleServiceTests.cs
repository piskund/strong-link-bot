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

        // SuddenDeathScores should be cleared
        Assert.Equal(0, session.Players[0].SuddenDeathScore);
        Assert.Equal(0, session.Players[1].SuddenDeathScore);
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

        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Topic = "History", Text = "Q1?", Answer = "A1" },
            new Question { Topic = "History", Text = "Q2?", Answer = "A2" },
            new Question { Topic = "History", Text = "Q3?", Answer = "A3" },
            new Question { Topic = "History", Text = "Q4?", Answer = "A4" },
            new Question { Topic = "History", Text = "Q5?", Answer = "A5" }
        });
        session.QuestionsByTour[2] = new Queue<Question>(new[]
        {
            new Question { Topic = "Geography", Text = "Q6?", Answer = "A6" }
        });

        session.TurnQueue.Enqueue(1000);
        session.TurnQueue.Enqueue(1001);
        session.TurnQueue.Enqueue(1002);

        _sentMessages.Clear();

        // Round 1: All answer wrong (scores stay 0,0,0)
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "WRONG", CancellationToken.None);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1001, "WRONG", CancellationToken.None);

        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1002, "WRONG", CancellationToken.None);

        // All still have SuddenDeathScore=0, should still be in sudden death
        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.Equal(0, session.Players[0].SuddenDeathScore);
        Assert.Equal(0, session.Players[1].SuddenDeathScore);
        Assert.Equal(0, session.Players[2].SuddenDeathScore);

        // Round 2: Player0 answers correctly
        await _service.AdvanceRoundAsync(session, CancellationToken.None);
        await _service.HandleAnswerAsync(session, 1000, "A4", CancellationToken.None);

        // Now Player0=1, others=0 - should eliminate Player1 and Player2
        Assert.Equal(PlayerStatus.Active, session.Players[0].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[1].Status);
        Assert.Equal(PlayerStatus.Eliminated, session.Players[2].Status);

        Assert.Equal(GameStatus.InProgress, session.Status);
        Assert.Equal(2, session.CurrentTour);
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

        // SuddenDeathScores should be cleared
        Assert.Equal(0, session.Players[0].SuddenDeathScore);
        Assert.Equal(0, session.Players[1].SuddenDeathScore);
    }

    private static GameSession CreateSession(int players, int tours = 1)
    {
        var session = new GameSession
        {
            ChatId = 123,
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = new[] { "History", "Geography" },
            Tours = tours,
            RoundsPerTour = 1,
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

