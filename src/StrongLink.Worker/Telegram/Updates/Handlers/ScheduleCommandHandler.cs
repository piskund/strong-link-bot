using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class ScheduleCommandHandler : CommandHandlerBase
{
    private readonly GameOptions _gameOptions;
    private readonly ILogger<ScheduleCommandHandler> _logger;

    public ScheduleCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IOptions<GameOptions> gameOptions,
        ILogger<ScheduleCommandHandler> logger)
        : base(client, localization, repository)
    {
        _gameOptions = gameOptions.Value;
        _logger = logger;
    }

    public override string Command => "/schedule";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /schedule command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var session = await Repository.LoadAsync(chatId, cancellationToken);
        var language = session?.Language ?? Domain.GameLanguage.Russian;

        if (!_gameOptions.EnableScheduledGames)
        {
            var disabledMessage = language == Domain.GameLanguage.Russian
                ? "⚙️ Запланированные игры отключены в конфигурации бота."
                : "⚙️ Scheduled games are disabled in bot configuration.";

            await Client.SendTextMessageAsync(chatId, disabledMessage, cancellationToken: cancellationToken);
            return;
        }

        // Calculate next scheduled time
        var now = DateTime.UtcNow;
        var today = now.Date;
        var scheduledTimeToday = today.Add(_gameOptions.ScheduledGameTimeUtc);

        DateTime nextScheduledTime;
        if (now < scheduledTimeToday)
        {
            // Today's game hasn't happened yet
            nextScheduledTime = scheduledTimeToday;
        }
        else
        {
            // Today's game has passed, show tomorrow's
            nextScheduledTime = scheduledTimeToday.AddDays(1);
        }

        var timeUntilNext = nextScheduledTime - now;
        var hoursUntil = (int)timeUntilNext.TotalHours;
        var minutesUntil = (int)timeUntilNext.TotalMinutes % 60;

        // Check if a scheduled game is currently waiting for players
        bool isWaitingForPlayers = false;
        DateTimeOffset? autoStartTime = null;

        if (session != null &&
            session.Status == Domain.GameStatus.AwaitingPlayers &&
            session.Metadata.TryGetValue("IsScheduledGame", out var isScheduledObj) &&
            isScheduledObj is bool isScheduled && isScheduled)
        {
            isWaitingForPlayers = true;
            if (session.Metadata.TryGetValue("ScheduledAutoStartTime", out var autoStartObj) &&
                autoStartObj is string autoStartStr &&
                DateTimeOffset.TryParse(autoStartStr, out var parsedAutoStartTime))
            {
                autoStartTime = parsedAutoStartTime;
            }
        }

        string message_text;
        if (language == Domain.GameLanguage.Russian)
        {
            if (isWaitingForPlayers && autoStartTime.HasValue)
            {
                var remainingMinutes = (int)(autoStartTime.Value - DateTimeOffset.UtcNow).TotalMinutes;
                message_text = $"⏰ Запланированная игра активна!\n\n" +
                              $"Игроков: {session?.Players.Count ?? 0}\n" +
                              $"Автостарт через: {remainingMinutes} мин.\n\n" +
                              $"Используйте /join чтобы присоединиться!";
            }
            else
            {
                message_text = $"📅 Расписание игр\n\n" +
                              $"🕐 Время начала: {_gameOptions.ScheduledGameTimeUtc:hh\\:mm} UTC ежедневно\n" +
                              $"⏱️ Время ожидания: {_gameOptions.ScheduledGameWaitMinutes} минут\n\n" +
                              $"⏰ Следующая игра через: {hoursUntil}ч {minutesUntil}мин\n" +
                              $"📍 Точное время: {nextScheduledTime:yyyy-MM-dd HH:mm} UTC\n\n" +
                              $"После начала у вас будет {_gameOptions.ScheduledGameWaitMinutes} минут чтобы присоединиться с помощью /join";
            }
        }
        else
        {
            if (isWaitingForPlayers && autoStartTime.HasValue)
            {
                var remainingMinutes = (int)(autoStartTime.Value - DateTimeOffset.UtcNow).TotalMinutes;
                message_text = $"⏰ Scheduled game is active!\n\n" +
                              $"Players: {session?.Players.Count ?? 0}\n" +
                              $"Auto-start in: {remainingMinutes} min.\n\n" +
                              $"Use /join to participate!";
            }
            else
            {
                message_text = $"📅 Game Schedule\n\n" +
                              $"🕐 Start time: {_gameOptions.ScheduledGameTimeUtc:hh\\:mm} UTC daily\n" +
                              $"⏱️ Wait time: {_gameOptions.ScheduledGameWaitMinutes} minutes\n\n" +
                              $"⏰ Next game in: {hoursUntil}h {minutesUntil}m\n" +
                              $"📍 Exact time: {nextScheduledTime:yyyy-MM-dd HH:mm} UTC\n\n" +
                              $"After start, you'll have {_gameOptions.ScheduledGameWaitMinutes} minutes to join using /join";
            }
        }

        await Client.SendTextMessageAsync(chatId, message_text, cancellationToken: cancellationToken);
    }
}
