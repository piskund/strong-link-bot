using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using StrongLink.Worker.Telegram.Updates.Handlers;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StrongLink.Worker.Tests.Telegram.CommandHandlers;

public class PreparePoolCommandHandlerTests
{
    private readonly Mock<ITelegramBotClient> _client;
    private readonly Mock<IGameSessionRepository> _repository;
    private readonly Mock<IQuestionPoolRepository> _poolRepository;
    private readonly QuestionProviderFactory _factory;
    private readonly Mock<IQuestionProvider> _questionProvider;
    private readonly ILocalizationService _localization;
    private readonly PreparePoolCommandHandler _handler;
    private readonly List<string> _sentMessages;

    public PreparePoolCommandHandlerTests()
    {
        _client = new Mock<ITelegramBotClient>();
        _repository = new Mock<IGameSessionRepository>();
        _poolRepository = new Mock<IQuestionPoolRepository>();

        // Create mock question provider
        _questionProvider = new Mock<IQuestionProvider>();
        _questionProvider.Setup(p => p.Mode).Returns(QuestionSourceMode.AI);

        // Create real factory with mocked providers
        _factory = new QuestionProviderFactory(new[] { _questionProvider.Object });

        _localization = new LocalizationService();
        _sentMessages = new List<string>();

        // Capture sent messages
        _client.Setup(c => c.MakeRequestAsync(
                It.IsAny<SendMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<IRequest<Message>, CancellationToken>((request, _) =>
            {
                if (request is SendMessageRequest sendRequest)
                {
                    _sentMessages.Add(sendRequest.Text);
                }
            })
            .ReturnsAsync(new Message());

        _handler = new PreparePoolCommandHandler(
            _client.Object,
            _localization,
            _repository.Object,
            _factory,
            _poolRepository.Object,
            NullLogger<PreparePoolCommandHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_WhenNoSession_DoesNothing()
    {
        // Arrange
        var update = CreateUpdate();
        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.Empty(_sentMessages);
        _repository.Verify(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenNoPlayers_StillPreparesQuestions()
    {
        // Arrange
        var update = CreateUpdate();
        var session = CreateSession(12345);
        // No players in session - but that's OK, we can still prepare questions

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _poolRepository.Setup(p => p.GetPoolStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0));

        _poolRepository.Setup(p => p.GetArchivedQuestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());

        _poolRepository.Setup(p => p.SelectQuestionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());

        var questions = new Dictionary<int, List<Question>>
        {
            { 1, new List<Question> { new Question { Topic = "General", Text = "Q1?", Answer = "A1" } } }
        };

        _questionProvider.Setup(p => p.PrepareQuestionPoolAsync(
                It.IsAny<string[]>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<Player>>(),
                It.IsAny<GameLanguage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(questions);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => savedSession = s)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(savedSession);
        Assert.Equal(GameStatus.ReadyToStart, savedSession.Status);
        Assert.NotEmpty(savedSession.QuestionsByTour);
        _repository.Verify(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithPlayers_PreparesQuestionsAndUpdatesStatus()
    {
        // Arrange
        var update = CreateUpdate();
        var session = CreateSession(12345);
        session.Players.Add(new Player { Id = 1, DisplayName = "Player1", Status = PlayerStatus.Active });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _poolRepository.Setup(p => p.GetPoolStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0));

        _poolRepository.Setup(p => p.GetArchivedQuestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());

        _poolRepository.Setup(p => p.SelectQuestionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());

        var questions = new Dictionary<int, List<Question>>
        {
            { 1, new List<Question> { new Question { Topic = "General", Text = "Q1?", Answer = "A1" } } }
        };

        _questionProvider.Setup(p => p.PrepareQuestionPoolAsync(
                It.IsAny<string[]>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<Player>>(),
                It.IsAny<GameLanguage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(questions);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => savedSession = s)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(savedSession);
        Assert.Equal(GameStatus.ReadyToStart, savedSession.Status);
        Assert.NotEmpty(savedSession.QuestionsByTour);
        Assert.Equal(2, _sentMessages.Count); // Preparing message + Ready message
        _repository.Verify(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ReusesQuestionsFromPool_WhenAvailable()
    {
        // Arrange
        var update = CreateUpdate();
        var session = CreateSession(12345);
        session.Players.Add(new Player { Id = 1, DisplayName = "Player1", Status = PlayerStatus.Active });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _poolRepository.Setup(p => p.GetPoolStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((10, 5)); // 10 unused, 5 archived

        _poolRepository.Setup(p => p.GetArchivedQuestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());

        var poolQuestions = new List<Question>
        {
            new Question { Topic = "General", Text = "Q1?", Answer = "A1" },
            new Question { Topic = "General", Text = "Q2?", Answer = "A2" },
            new Question { Topic = "General", Text = "Q3?", Answer = "A3" }
        };

        _poolRepository.Setup(p => p.SelectQuestionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(poolQuestions);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => savedSession = s)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(savedSession);
        Assert.Equal(GameStatus.ReadyToStart, savedSession.Status);

        // Should NOT generate new questions when enough in pool
        _questionProvider.Verify(p => p.PrepareQuestionPoolAsync(
            It.IsAny<string[]>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IReadOnlyList<Player>>(),
            It.IsAny<GameLanguage>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Should reuse from pool
        _poolRepository.Verify(p => p.SelectQuestionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_GeneratesNewQuestions_WhenPoolInsufficient()
    {
        // Arrange
        var update = CreateUpdate();
        var session = CreateSession(12345);
        session.Players.Add(new Player { Id = 1, DisplayName = "Player1", Status = PlayerStatus.Active });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _poolRepository.Setup(p => p.GetPoolStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((1, 0)); // Only 1 question in pool, need 3

        _poolRepository.Setup(p => p.GetArchivedQuestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Question>());

        var poolQuestions = new List<Question>
        {
            new Question { Topic = "General", Text = "Q1?", Answer = "A1" }
        };

        _poolRepository.Setup(p => p.SelectQuestionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(poolQuestions);

        var generatedQuestions = new Dictionary<int, List<Question>>
        {
            { 1, new List<Question>
                {
                    new Question { Topic = "General", Text = "Q2?", Answer = "A2" },
                    new Question { Topic = "General", Text = "Q3?", Answer = "A3" },
                    new Question { Topic = "General", Text = "Q4?", Answer = "A4" } // Extra question - will become surplus
                }
            }
        };

        _questionProvider.Setup(p => p.PrepareQuestionPoolAsync(
                It.IsAny<string[]>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<Player>>(),
                It.IsAny<GameLanguage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(generatedQuestions);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        // Should generate new questions
        _questionProvider.Verify(p => p.PrepareQuestionPoolAsync(
            It.IsAny<string[]>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IReadOnlyList<Player>>(),
            It.IsAny<GameLanguage>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Should add generated questions to pool
        _poolRepository.Verify(p => p.AddToUnusedPoolAsync(It.IsAny<IEnumerable<Question>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Update CreateUpdate()
    {
        return new Update
        {
            Message = new Message
            {
                Chat = new Chat { Id = 12345, Type = ChatType.Group },
                From = new User { Id = 123, Username = "testuser" },
                Text = "/prepare_pool"
            }
        };
    }

    private static GameSession CreateSession(long chatId)
    {
        return new GameSession
        {
            ChatId = chatId,
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = new[] { "General" },
            Tours = 1,
            RoundsPerTour = 3,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1,
            Status = GameStatus.AwaitingPlayers
        };
    }
}
