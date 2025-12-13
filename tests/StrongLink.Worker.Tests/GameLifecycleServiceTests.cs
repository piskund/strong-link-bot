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
    private readonly QuestionProviderFactory _questionProviderFactory = new(new List<IQuestionProvider>());
    private readonly LocalizationService _localization = new();
    private readonly GameLifecycleService _service;
    private readonly List<string> _sentMessages = new();

    public GameLifecycleServiceTests()
    {
        _messenger
            .Setup(m => m.SendAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<long, string, CancellationToken>((_, message, _) => _sentMessages.Add(message))
            .Returns(Task.CompletedTask);

        // Setup answer validator to validate correct answers as true
        _answerValidator
            .Setup(v => v.ValidateAnswerAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<GameLanguage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userAnswer, string correctAnswer, string question, GameLanguage language, CancellationToken ct) =>
                string.Equals(userAnswer, correctAnswer, StringComparison.OrdinalIgnoreCase));

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

    private static GameSession CreateSession(int players)
    {
        var session = new GameSession
        {
            ChatId = 123,
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = new[] { "History" },
            Tours = 1,
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

        return session;
    }
}

