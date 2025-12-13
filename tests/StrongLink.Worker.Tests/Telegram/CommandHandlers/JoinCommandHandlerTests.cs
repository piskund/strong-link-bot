using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using StrongLink.Worker.Telegram.Updates.Handlers;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StrongLink.Worker.Tests.Telegram.CommandHandlers;

public class JoinCommandHandlerTests
{
    private readonly Mock<ITelegramBotClient> _client;
    private readonly Mock<IGameSessionRepository> _repository;
    private readonly ILocalizationService _localization;
    private readonly IOptions<BotOptions> _botOptions;
    private readonly IOptions<GameOptions> _gameOptions;
    private readonly JoinCommandHandler _handler;
    private readonly List<string> _sentMessages;

    public JoinCommandHandlerTests()
    {
        _client = new Mock<ITelegramBotClient>();
        _repository = new Mock<IGameSessionRepository>();
        _localization = new LocalizationService();
        _sentMessages = new List<string>();

        _botOptions = Options.Create(new BotOptions
        {
            DefaultLanguage = GameLanguage.English,
            QuestionSource = QuestionSourceMode.AI
        });

        _gameOptions = Options.Create(new GameOptions
        {
            Topics = new[] { "General" },
            Tours = 1,
            RoundsPerTour = 3,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1
        });

        // Capture sent messages using the more flexible MakeRequestAsync
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

        _handler = new JoinCommandHandler(
            _client.Object,
            _localization,
            _repository.Object,
            _botOptions,
            _gameOptions,
            NullLogger<JoinCommandHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_WhenNoSessionExists_CreatesSessionAndAddsPlayer()
    {
        // Arrange
        var update = CreateUpdate(userId: 123, username: "testuser");
        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(savedSession);
        Assert.Single(savedSession.Players);
        Assert.Equal(123, savedSession.Players[0].Id);
        Assert.Equal("@testuser", savedSession.Players[0].DisplayName);
        Assert.Equal(PlayerStatus.Active, savedSession.Players[0].Status);
        Assert.Equal(GameStatus.AwaitingPlayers, savedSession.Status);
        Assert.Contains("@testuser", _sentMessages[0]);
        _repository.Verify(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenSessionExists_AddsPlayerToExistingSession()
    {
        // Arrange
        var update = CreateUpdate(userId: 456, username: "newplayer");
        var existingSession = CreateSession(12345);
        existingSession.Players.Add(new Player
        {
            Id = 123,
            DisplayName = "@existingplayer",
            Status = PlayerStatus.Active
        });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSession);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(savedSession);
        Assert.Equal(2, savedSession.Players.Count);
        Assert.Contains(savedSession.Players, p => p.Id == 456 && p.DisplayName == "@newplayer");
        Assert.Contains("@newplayer", _sentMessages[0]);
    }

    [Fact]
    public async Task HandleAsync_WhenPlayerAlreadyJoined_DoesNotAddDuplicate()
    {
        // Arrange
        var update = CreateUpdate(userId: 123, username: "testuser");
        var existingSession = CreateSession(12345);
        existingSession.Players.Add(new Player
        {
            Id = 123,
            DisplayName = "@testuser",
            Status = PlayerStatus.Active
        });

        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSession);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.Single(existingSession.Players);
        Assert.Contains("already", _sentMessages[0], StringComparison.OrdinalIgnoreCase);
        _repository.Verify(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenUserHasNoUsername_UsesFirstName()
    {
        // Arrange
        var update = CreateUpdate(userId: 789, username: null, firstName: "John");
        _repository.Setup(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((session, _) => savedSession = session)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert
        Assert.NotNull(savedSession);
        Assert.Single(savedSession.Players);
        Assert.Equal("John", savedSession.Players[0].DisplayName);
    }

    [Fact]
    public async Task HandleAsync_VerifiesSessionIsPersisted()
    {
        // Arrange
        var update = CreateUpdate(userId: 999, username: "verifyuser");

        var savedSession = CreateSession(12345);
        savedSession.Players.Add(new Player { Id = 999, DisplayName = "@verifyuser", Status = PlayerStatus.Active });

        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Return null on first load, saved session on second load (verification)
        _repository.SetupSequence(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null)
            .ReturnsAsync(savedSession);

        // Act
        await _handler.HandleAsync(update, CancellationToken.None);

        // Assert - LoadAsync should be called twice (initial + verification)
        _repository.Verify(r => r.LoadAsync(update.Message!.Chat.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static Update CreateUpdate(long userId, string? username, string? firstName = null)
    {
        return new Update
        {
            Message = new Message
            {
                Chat = new Chat { Id = 12345, Type = ChatType.Group },
                From = new User
                {
                    Id = userId,
                    Username = username,
                    FirstName = firstName ?? "Test"
                },
                Text = "/join"
            }
        };
    }

    private GameSession CreateSession(long chatId)
    {
        return new GameSession
        {
            ChatId = chatId,
            Language = _botOptions.Value.DefaultLanguage,
            QuestionSourceMode = _botOptions.Value.QuestionSource,
            Topics = _gameOptions.Value.Topics,
            Tours = _gameOptions.Value.Tours,
            RoundsPerTour = _gameOptions.Value.RoundsPerTour,
            AnswerTimeoutSeconds = _gameOptions.Value.AnswerTimeoutSeconds,
            EliminateLowest = _gameOptions.Value.EliminateLowest,
            Status = GameStatus.AwaitingPlayers
        };
    }
}
