using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Tests.Services;

public class ScheduledGameServiceTests
{
    private readonly Mock<IGameSessionRepository> _repository;
    private readonly Mock<IChatMessenger> _messenger;
    private readonly Mock<IGameLifecycleService> _lifecycleService;
    private readonly QuestionProviderFactory _questionProviderFactory;
    private readonly Mock<IQuestionPoolRepository> _poolRepository;
    private readonly GameOptions _gameOptions;
    private readonly List<string> _sentMessages;

    public ScheduledGameServiceTests()
    {
        _repository = new Mock<IGameSessionRepository>();
        _messenger = new Mock<IChatMessenger>();
        _lifecycleService = new Mock<IGameLifecycleService>();
        _questionProviderFactory = new QuestionProviderFactory(Array.Empty<IQuestionProvider>());
        _poolRepository = new Mock<IQuestionPoolRepository>();
        _sentMessages = new List<string>();

        _gameOptions = new GameOptions
        {
            EnableScheduledGames = true,
            ScheduledGameChatIds = new List<long> { 123456, 789012 },
            ScheduledGameTimeUtc = TimeSpan.FromHours(12), // 12:00 UTC
            ScheduledGameWaitMinutes = 30,
            Tours = 3,
            RoundsPerTour = 10,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1,
            Topics = new[] { "History", "Science", "Geography" }
        };

        _messenger
            .Setup(m => m.SendAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<long, string, CancellationToken>((_, message, _) => _sentMessages.Add(message))
            .ReturnsAsync(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNothing()
    {
        // Arrange
        var options = new GameOptions { EnableScheduledGames = false };
        var service = CreateService(Options.Create(options));

        // Act
        var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(100); // Give it time to start
        cts.Cancel();

        // Assert
        _repository.Verify(r => r.LoadAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _messenger.Verify(m => m.SendAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoChatIdsConfigured_DoesNothing()
    {
        // Arrange
        var options = new GameOptions
        {
            EnableScheduledGames = true,
            ScheduledGameChatIds = new List<long>() // Empty list
        };
        var service = CreateService(Options.Create(options));

        // Act
        var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        // Assert
        _repository.Verify(r => r.LoadAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _messenger.Verify(m => m.SendAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStartedAfterScheduledTime_DoesNotInitializeNewGame()
    {
        // Arrange: Current time is 3 PM, scheduled time is 12 PM (already passed)
        var now = DateTime.UtcNow;
        var scheduledTime = TimeSpan.FromHours(now.Hour - 3); // 3 hours ago
        var options = new GameOptions
        {
            EnableScheduledGames = true,
            ScheduledGameChatIds = new List<long> { 123456 },
            ScheduledGameTimeUtc = scheduledTime
        };

        // LoadAsync returns null — no existing session, so auto-start check is a no-op
        _repository.Setup(r => r.LoadAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        var service = CreateService(Options.Create(options));

        // Act
        var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);
        await Task.Delay(500); // Give it time to run the first check loop
        cts.Cancel();

        // Assert: InitializeScheduledGameAsync was NOT called (no SaveAsync, no game start message)
        // LoadAsync may be called by CheckAutoStartTimersAsync — that's fine and expected.
        _repository.Verify(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Never);
        _messenger.Verify(m => m.SendAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InitializeScheduledGame_CreatesNewSession_WhenNoExistingSession()
    {
        // Arrange
        _repository.Setup(r => r.LoadAsync(123456, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameSession?)null);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => savedSession = s)
            .Returns(Task.CompletedTask);

        var service = CreateService(Options.Create(_gameOptions));

        // Use reflection to call the private method for testing
        var method = typeof(ScheduledGameService).GetMethod("InitializeScheduledGameAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        await (Task)method!.Invoke(service, new object[] { 123456L, CancellationToken.None })!;

        // Assert
        Assert.NotNull(savedSession);
        Assert.Equal(GameStatus.AwaitingPlayers, savedSession.Status);
        Assert.Equal(123456, savedSession.ChatId);
        Assert.True(savedSession.Metadata.ContainsKey("IsScheduledGame"));
        Assert.True(savedSession.Metadata.ContainsKey("ScheduledAutoStartTime"));

        // Should send multiple messages: initial announcement + question preparation start + error/completion
        Assert.True(_sentMessages.Count >= 2, $"Expected at least 2 messages, but got {_sentMessages.Count}");
        // First message could be in Russian or English
        Assert.True(_sentMessages[0].Contains("game", StringComparison.OrdinalIgnoreCase) ||
                    _sentMessages[0].Contains("игра", StringComparison.OrdinalIgnoreCase),
                    $"Expected first message to contain 'game' or 'игра', but got: {_sentMessages[0]}");
    }

    [Fact]
    public async Task AutoStart_WithNoPlayers_CancelsGame()
    {
        // Arrange
        var session = new GameSession
        {
            ChatId = 123456,
            Status = GameStatus.AwaitingPlayers,
            Language = GameLanguage.English
        };
        session.Metadata["IsScheduledGame"] = true;
        session.Metadata["ScheduledAutoStartTime"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"); // Time has passed

        _repository.Setup(r => r.LoadAsync(123456, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        GameSession? savedSession = null;
        _repository.Setup(r => r.SaveAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => savedSession = s)
            .Returns(Task.CompletedTask);

        var service = CreateService(Options.Create(_gameOptions));
        var method = typeof(ScheduledGameService).GetMethod("AutoStartScheduledGameAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        await (Task)method!.Invoke(service, new object[] { session, CancellationToken.None })!;

        // Assert
        Assert.Equal(GameStatus.Cancelled, savedSession!.Status);
        Assert.Contains(_sentMessages, m => m.Contains("No one joined") || m.Contains("Никто не присоединился"));
        _lifecycleService.Verify(l => l.StartGameAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AutoStart_WithPlayers_StartsGame()
    {
        // Arrange
        var session = new GameSession
        {
            ChatId = 123456,
            Status = GameStatus.AwaitingPlayers,
            Language = GameLanguage.English
        };
        session.Players.Add(new Player
        {
            Id = 1,
            DisplayName = "Player1",
            Status = PlayerStatus.Active
        });

        // Pre-populate questions so it doesn't try to prepare them
        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Text = "Test Question 1", Answer = "Answer 1", Topic = "Test Topic" },
            new Question { Text = "Test Question 2", Answer = "Answer 2", Topic = "Test Topic" }
        });

        session.Metadata["IsScheduledGame"] = true;
        session.Metadata["ScheduledAutoStartTime"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");

        _repository.Setup(r => r.LoadAsync(123456, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var service = CreateService(Options.Create(_gameOptions));
        var method = typeof(ScheduledGameService).GetMethod("AutoStartScheduledGameAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        await (Task)method!.Invoke(service, new object[] { session, CancellationToken.None })!;

        // Assert
        _lifecycleService.Verify(l => l.StartGameAsync(session, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_sentMessages, m => m.Contains("auto-start") || m.Contains("автоматический"));
    }

    [Fact]
    public async Task AutoStart_ClearsScheduledGameMetadata()
    {
        // Arrange
        var session = new GameSession
        {
            ChatId = 123456,
            Status = GameStatus.AwaitingPlayers,
            Language = GameLanguage.English
        };
        session.Players.Add(new Player { Id = 1, DisplayName = "Player1", Status = PlayerStatus.Active });

        // Pre-populate questions so it doesn't try to prepare them
        session.QuestionsByTour[1] = new Queue<Question>(new[]
        {
            new Question { Text = "Test Question 1", Answer = "Answer 1", Topic = "Test Topic" }
        });

        session.Metadata["IsScheduledGame"] = true;
        session.Metadata["ScheduledAutoStartTime"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");

        GameSession? savedSession = null;
        _lifecycleService
            .Setup(l => l.StartGameAsync(It.IsAny<GameSession>(), It.IsAny<CancellationToken>()))
            .Callback<GameSession, CancellationToken>((s, _) => savedSession = s)
            .Returns(Task.CompletedTask);

        var service = CreateService(Options.Create(_gameOptions));
        var method = typeof(ScheduledGameService).GetMethod("AutoStartScheduledGameAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        await (Task)method!.Invoke(service, new object[] { session, CancellationToken.None })!;

        // Assert
        Assert.False(session.Metadata.ContainsKey("IsScheduledGame"));
        Assert.False(session.Metadata.ContainsKey("ScheduledAutoStartTime"));
        Assert.True(session.Metadata.ContainsKey("WasScheduledGame"));
    }

    private ScheduledGameService CreateService(IOptions<GameOptions> options)
    {
        return new ScheduledGameService(
            _repository.Object,
            _messenger.Object,
            _lifecycleService.Object,
            _questionProviderFactory,
            _poolRepository.Object,
            options,
            NullLogger<ScheduledGameService>.Instance);
    }
}
