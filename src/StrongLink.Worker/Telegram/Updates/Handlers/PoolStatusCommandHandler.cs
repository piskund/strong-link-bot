using Microsoft.Extensions.Logging;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class PoolStatusCommandHandler : CommandHandlerBase
{
    private readonly ILogger<PoolStatusCommandHandler> _logger;
    private readonly IQuestionPoolRepository _poolRepository;

    public PoolStatusCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IQuestionPoolRepository poolRepository,
        ILogger<PoolStatusCommandHandler> logger)
        : base(client, localization, repository)
    {
        _poolRepository = poolRepository;
        _logger = logger;
    }

    public override string Command => "/pool_status";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /pool_status command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        try
        {
            var (unused, archived) = await _poolRepository.GetPoolStatsAsync(cancellationToken);

            var status = $"📊 *Статус пула вопросов*\n\n" +
                        $"🟢 Неиспользованные: {unused} вопросов\n" +
                        $"📦 Архивные: {archived} вопросов\n" +
                        $"📈 Всего: {unused + archived} вопросов";

            _logger.LogInformation("Pool status for chat {ChatId}: {Unused} unused, {Archived} archived",
                chatId, unused, archived);

            await Client.SendTextMessageAsync(
                chatId,
                status,
                parseMode: global::Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pool status for chat {ChatId}", chatId);
            await Client.SendTextMessageAsync(
                chatId,
                "Не удалось получить статус пула вопросов.",
                cancellationToken: cancellationToken);
        }
    }
}
