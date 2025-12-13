using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using StrongLink.Worker.Telegram.Updates.Handlers;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StrongLink.Worker.Tests.Telegram.CommandHandlers;

public class StartGameCommandHandlerTests
{
    private readonly Mock<ITelegramBotClient> _client;
    private readonly Mock<IGameSessionRepository> _repository;
    private readonly Mock<IGameLifecycleService> _lifecycleService;
    private readonly ILocalizationService _localization;
    private readonly StartGameCommandHandler _handler;

    public StartGameCommandHandlerTests()
    {
        _client = new Mock<ITelegramBotClient>();
        _repository = new Mock<IGameSessionRepository>();
        _lifecycleService = new Mock<IGameLifecycleService>();
        _localization = new LocalizationService();

        _client.Setup(c => c.MakeRequestAsync(
                It.IsAny<SendMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message());

        var botOptions = Microsoft.Extensions.Options.Options.Create(new StrongLink.Worker.Configuration.BotOptions());
        _handler = new StartGameCommandHandler(
            _client.Object,
            _localization,
            _repository.Object,
            _lifecycleService.Object,
            NullLogger<StartGameCommandHandler>.Instance,
            botOptions);
    }

    [Fact]
    public async Task HandleAsync_WhenNoSession_DoesNotStartGame()
    {
        // Arrange
        var update = CreateUpdate();
        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        _lifecycleService.Verify(s => s.StartGameAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenSessionExists_CallsLifecycleService()
    {
        // Arrange
        var update = CreateUpdate();
        var session = CreateSession(12345);
        session.Players.Add(new Player { Id = 1, DisplayName = "Player1", Status = PlayerStatus.Active });
        session.Status = GameStatus.ReadyToStart;
        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Topic = "General", Text = "Q1?", Answer = "A1" }
        });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        _lifecycleService.Setup(s => s.StartGameAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        _lifecycleService.Verify(s => s.StartGameAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_PassesCorrectSessionToLifecycle()
    {
        // Arrange
        var update = CreateUpdate();
        var session = CreateSession(999);
        session.Players.Add(new Player { Id = 1, DisplayName = "Player1", Status = PlayerStatus.Active });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        GameSession? passedSession = null;
        _lifecycleService.Setup(s => s.StartGameAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => passedSession = s)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(passedSession);
        Assert.Equal(999, passedSession.ChatId);
        Assert.Single(passedSession.Players);
    }

    [Fact]
    public void Command_ReturnsCorrectCommandString()
    {
        // Assert
        Assert.Equal("/begin", _handler.Command);
    }

    private static Update CreateUpdate()
    {
        return new Update
        {
            Message = new Message
            {
                Chat = new Chat { Id = 12345, Type = ChatType.Group },
                From = new User { Id = 123, Username = "testuser" },
                Text = "/begin"
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
