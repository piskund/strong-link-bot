using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Persistence;

namespace StrongLink.Worker.Services;

public sealed class ScheduledGameService : BackgroundService
{
    private readonly IGameSessionRepository _repository;
    private readonly IChatMessenger _messenger;
    private readonly IGameLifecycleService _lifecycleService;
    private readonly GameOptions _gameOptions;
    private readonly ILogger<ScheduledGameService> _logger;
    private DateTime _lastCheckDate = DateTime.MinValue;

    public ScheduledGameService(
        IGameSessionRepository repository,
        IChatMessenger messenger,
        IGameLifecycleService lifecycleService,
        IOptions<GameOptions> gameOptions,
        ILogger<ScheduledGameService> logger)
    {
        _repository = repository;
        _messenger = messenger;
        _lifecycleService = lifecycleService;
        _gameOptions = gameOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_gameOptions.EnableScheduledGames)
        {
            _logger.LogInformation("Scheduled games are disabled in configuration");
            return;
        }

        _logger.LogInformation("Scheduled game service started. Games will start at {Time} UTC daily, " +
            "with {WaitMinutes} minutes for players to join",
            _gameOptions.ScheduledGameTimeUtc, _gameOptions.ScheduledGameWaitMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndStartScheduledGamesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled game service");
            }

            // Check every minute
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task CheckAndStartScheduledGamesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var currentDate = now.Date;
        var scheduledTime = currentDate.Add(_gameOptions.ScheduledGameTimeUtc);

        // Check if we've already processed today's scheduled start
        if (_lastCheckDate == currentDate)
        {
            // Already processed today, check for auto-start timers
            await CheckAutoStartTimersAsync(cancellationToken);
            return;
        }

        // Check if it's time to trigger the scheduled start
        if (now >= scheduledTime && _lastCheckDate < currentDate)
        {
            _logger.LogInformation("Scheduled game time reached. Triggering scheduled game initialization");
            _lastCheckDate = currentDate;

            // Load all sessions and initialize scheduled games
            var allChatIds = await GetAllActiveChatIdsAsync(cancellationToken);
            foreach (var chatId in allChatIds)
            {
                try
                {
                    await InitializeScheduledGameAsync(chatId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize scheduled game for chat {ChatId}", chatId);
                }
            }
        }

        // Check for games that need to auto-start
        await CheckAutoStartTimersAsync(cancellationToken);
    }

    private async Task InitializeScheduledGameAsync(long chatId, CancellationToken cancellationToken)
    {
        var session = await _repository.LoadAsync(chatId, cancellationToken);

        // Only initialize if there's no active game
        if (session == null || session.Status == GameStatus.NotConfigured ||
            session.Status == GameStatus.Completed || session.Status == GameStatus.Cancelled)
        {
            _logger.LogInformation("Initializing scheduled game for chat {ChatId}", chatId);

            // Create a new session if needed
            if (session == null || session.Status == GameStatus.Completed || session.Status == GameStatus.Cancelled)
            {
                // We need to create a new session, but we need to reuse previous settings if available
                // For now, create a basic session
                session = new GameSession
                {
                    ChatId = chatId,
                    Status = GameStatus.AwaitingPlayers,
                    Tours = _gameOptions.Tours,
                    RoundsPerTour = _gameOptions.RoundsPerTour,
                    AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                    EliminateLowest = _gameOptions.EliminateLowest,
                    Topics = _gameOptions.Topics.ToList()
                };
            }
            else
            {
                session.Status = GameStatus.AwaitingPlayers;
            }

            // Mark as scheduled game and set auto-start time
            var autoStartTime = DateTimeOffset.UtcNow.AddMinutes(_gameOptions.ScheduledGameWaitMinutes);
            session.Metadata["IsScheduledGame"] = true;
            session.Metadata["ScheduledAutoStartTime"] = autoStartTime.ToString("o");

            await _repository.SaveAsync(session, cancellationToken);

            // Send notification to chat
            var message = session.Language == GameLanguage.Russian
                ? $"🎮 Запланированная игра начинается!\n\n" +
                  $"Используйте /join чтобы присоединиться.\n" +
                  $"Игра автоматически начнется через {_gameOptions.ScheduledGameWaitMinutes} минут, если присоединится хотя бы 1 игрок."
                : $"🎮 Scheduled game is starting!\n\n" +
                  $"Use /join to participate.\n" +
                  $"The game will automatically begin in {_gameOptions.ScheduledGameWaitMinutes} minutes if at least 1 player joins.";

            await _messenger.SendAsync(chatId, message, cancellationToken);
        }
    }

    private async Task CheckAutoStartTimersAsync(CancellationToken cancellationToken)
    {
        var allChatIds = await GetAllActiveChatIdsAsync(cancellationToken);

        foreach (var chatId in allChatIds)
        {
            try
            {
                var session = await _repository.LoadAsync(chatId, cancellationToken);
                if (session == null) continue;

                // Check if this is a scheduled game waiting to auto-start
                if (session.Status == GameStatus.AwaitingPlayers &&
                    session.Metadata.TryGetValue("IsScheduledGame", out var isScheduledObj) &&
                    isScheduledObj is bool isScheduled && isScheduled &&
                    session.Metadata.TryGetValue("ScheduledAutoStartTime", out var autoStartObj) &&
                    autoStartObj is string autoStartStr &&
                    DateTimeOffset.TryParse(autoStartStr, out var autoStartTime))
                {
                    if (DateTimeOffset.UtcNow >= autoStartTime)
                    {
                        _logger.LogInformation("Auto-start time reached for scheduled game in chat {ChatId}", chatId);
                        await AutoStartScheduledGameAsync(session, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking auto-start timer for chat {ChatId}", chatId);
            }
        }
    }

    private async Task AutoStartScheduledGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        // Clear scheduled game metadata
        session.Metadata.Remove("IsScheduledGame");
        session.Metadata.Remove("ScheduledAutoStartTime");

        if (session.Players.Count == 0)
        {
            // No players joined, cancel the game
            _logger.LogInformation("No players joined scheduled game in chat {ChatId}. Cancelling.", session.ChatId);

            session.Status = GameStatus.Cancelled;
            await _repository.SaveAsync(session, cancellationToken);

            var message = session.Language == GameLanguage.Russian
                ? "⏰ Время вышло! Никто не присоединился к запланированной игре. Игра отменена."
                : "⏰ Time's up! No one joined the scheduled game. Game cancelled.";

            await _messenger.SendAsync(session.ChatId, message, cancellationToken);
        }
        else
        {
            // Start the game
            _logger.LogInformation("Auto-starting scheduled game in chat {ChatId} with {PlayerCount} player(s)",
                session.ChatId, session.Players.Count);

            var message = session.Language == GameLanguage.Russian
                ? $"⏰ Время вышло! Игра начинается с {session.Players.Count} игроком(ами)!"
                : $"⏰ Time's up! Starting game with {session.Players.Count} player(s)!";

            await _messenger.SendAsync(session.ChatId, message, cancellationToken);

            // Start the game
            await _lifecycleService.StartGameAsync(session, cancellationToken);
        }
    }

    private Task<List<long>> GetAllActiveChatIdsAsync(CancellationToken cancellationToken)
    {
        // This is a simplified implementation
        // In a real scenario, you might want to track chat IDs in a separate collection
        // For now, we'll use a metadata file or similar approach

        // Check if the repository is JsonGameSessionRepository which stores sessions in files
        if (_repository is JsonGameSessionRepository jsonRepo)
        {
            // Get all session files
            var dataDir = Path.Combine(AppContext.BaseDirectory, "data", "sessions");
            if (!Directory.Exists(dataDir))
            {
                return Task.FromResult(new List<long>());
            }

            var chatIds = new List<long>();
            foreach (var file in Directory.GetFiles(dataDir, "*.json"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (long.TryParse(fileName, out var chatId))
                {
                    chatIds.Add(chatId);
                }
            }
            return Task.FromResult(chatIds);
        }

        return Task.FromResult(new List<long>());
    }
}
