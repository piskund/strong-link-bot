using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class PoolClearCommandHandler : CommandHandlerBase
{
    private readonly ILogger<PoolClearCommandHandler> _logger;
    private readonly IQuestionPoolRepository _poolRepository;

    public PoolClearCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IQuestionPoolRepository poolRepository,
        ILogger<PoolClearCommandHandler> logger,
        IOptions<BotOptions> botOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _poolRepository = poolRepository;
        _logger = logger;
    }

    public override string Command => "/pool_clear";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /pool_clear command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        try
        {
            // Check if command includes "archive" parameter to clear both pools
            var clearArchive = message.Text?.Contains("archive", StringComparison.OrdinalIgnoreCase) ?? false;

            await _poolRepository.ClearPoolAsync(clearArchive, cancellationToken);

            var resultMessage = clearArchive
                ? "✅ Пул неиспользованных и архивных вопросов очищен."
                : "✅ Пул неиспользованных вопросов очищен. Архив сохранён.\n" +
                  "Чтобы очистить и архив, используйте: `/pool_clear archive`";

            _logger.LogInformation("Pool cleared for chat {ChatId}. Archive cleared: {ClearArchive}",
                chatId, clearArchive);

            await Client.SendTextMessageAsync(
                chatId,
                resultMessage,
                parseMode: global::Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear pool for chat {ChatId}", chatId);
            await Client.SendTextMessageAsync(
                chatId,
                "Не удалось очистить пул вопросов.",
                cancellationToken: cancellationToken);
        }
    }
}
