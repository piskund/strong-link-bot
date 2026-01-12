using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class TestScheduledCommandHandler : CommandHandlerBase
{
    private readonly IChatMessenger _messenger;
    private readonly IGameLifecycleService _lifecycleService;
    private readonly IQuestionPoolRepository _poolRepository;
    private readonly GameOptions _gameOptions;
    private readonly BotOptions _botOptions;
    private readonly ILogger<TestScheduledCommandHandler> _logger;

    public TestScheduledCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IChatMessenger messenger,
        IGameLifecycleService lifecycleService,
        IQuestionPoolRepository poolRepository,
        IOptions<GameOptions> gameOptions,
        IOptions<BotOptions> botOptions,
        ILogger<TestScheduledCommandHandler> logger)
        : base(client, localization, repository, botOptions.Value)
    {
        _messenger = messenger;
        _lifecycleService = lifecycleService;
        _poolRepository = poolRepository;
        _gameOptions = gameOptions.Value;
        _botOptions = botOptions.Value;
        _logger = logger;
    }

    public override string Command => "/testscheduled";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /testscheduled command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        if (!_gameOptions.EnableScheduledGames)
        {
            await Client.SendTextMessageAsync(chatId,
                "⚠️ Scheduled games are disabled in configuration.",
                cancellationToken: cancellationToken);
            return;
        }

        // Get wait minutes from command argument or use default (2 minutes for testing)
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        var waitMinutes = 2; // Default to 2 minutes for testing

        if (parts.Length > 1 && int.TryParse(parts[1], out var parsed) && parsed > 0 && parsed <= 60)
        {
            waitMinutes = parsed;
        }

        // Check existing session
        var session = await Repository.LoadAsync(chatId, cancellationToken);

        // Only initialize if there's no active game
        if (session != null &&
            (session.Status == GameStatus.InProgress ||
             session.Status == GameStatus.SuddenDeath ||
             session.Status == GameStatus.AwaitingPlayers))
        {
            await Client.SendTextMessageAsync(chatId,
                "⚠️ There's already an active game or scheduled game in this chat.",
                cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("TEST: Initializing scheduled game for chat {ChatId}", chatId);

        // Use smart topic selection to prioritize topics with unused questions
        var selectedTopics = await TopicSelector.SelectOptimalTopicsAsync(
            _poolRepository,
            _gameOptions.Topics,
            _gameOptions.Tours,
            _logger,
            cancellationToken);

        // Create a new session if needed
        if (session == null || session.Status == GameStatus.Completed || session.Status == GameStatus.Cancelled)
        {
            session = new GameSession
            {
                ChatId = chatId,
                Language = _botOptions.DefaultLanguage,
                QuestionSourceMode = _botOptions.QuestionSource,
                Status = GameStatus.AwaitingPlayers,
                Tours = _gameOptions.Tours,
                RoundsPerTour = _gameOptions.RoundsPerTour,
                AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                EliminateLowest = _gameOptions.EliminateLowest,
                Topics = selectedTopics
            };
        }
        else
        {
            session.Status = GameStatus.AwaitingPlayers;
        }

        // Mark as scheduled game and set auto-start time
        var autoStartTime = DateTimeOffset.UtcNow.AddMinutes(waitMinutes);
        session.Metadata["IsScheduledGame"] = true;
        session.Metadata["ScheduledAutoStartTime"] = autoStartTime.ToString("o");

        await Repository.SaveAsync(session, cancellationToken);

        // Send notification to chat
        var messageText = session.Language == GameLanguage.Russian
            ? $"🎮 [ТЕСТ] Запланированная игра начинается!\n\n" +
              $"Используйте /join чтобы присоединиться.\n" +
              $"Игра автоматически начнется через {waitMinutes} минут, если присоединится хотя бы 1 игрок.\n\n" +
              $"⏰ Время автостарта: {autoStartTime:HH:mm:ss} UTC\n" +
              $"📊 ID чата: {chatId}"
            : $"🎮 [TEST] Scheduled game is starting!\n\n" +
              $"Use /join to participate.\n" +
              $"The game will automatically begin in {waitMinutes} minutes if at least 1 player joins.\n\n" +
              $"⏰ Auto-start time: {autoStartTime:HH:mm:ss} UTC\n" +
              $"📊 Chat ID: {chatId}";

        await _messenger.SendAsync(chatId, messageText, cancellationToken);

        _logger.LogInformation("TEST: Scheduled game initialized for chat {ChatId}. Auto-start at {Time}",
            chatId, autoStartTime);
    }
}
