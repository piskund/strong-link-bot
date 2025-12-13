using StrongLink.Worker.Configuration;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates;

public abstract class CommandHandlerBase : IUpdateHandler
{
    private readonly BotOptions? _botOptions;

    protected CommandHandlerBase(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository)
    {
        Client = client;
        Localization = localization;
        Repository = repository;
    }

    protected CommandHandlerBase(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        BotOptions botOptions)
        : this(client, localization, repository)
    {
        _botOptions = botOptions;
    }

    protected ITelegramBotClient Client { get; }

    protected ILocalizationService Localization { get; }

    protected IGameSessionRepository Repository { get; }

    public abstract string Command { get; }

    protected virtual bool RequiresAdmin => false;

    public bool CanHandle(Update update)
    {
        if (update.Message?.Text is null)
        {
            return false;
        }

        return update.Message.Text.StartsWith(Command, StringComparison.OrdinalIgnoreCase);
    }

    public async Task HandleAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Message is null)
        {
            return;
        }

        // Check admin authorization if required
        if (RequiresAdmin)
        {
            var userId = update.Message.From?.Id;
            var username = update.Message.From?.Username;
            var chatId = update.Message.Chat.Id;

            if (!await IsAuthorizedAsync(userId, username, chatId, cancellationToken))
            {
                await Client.SendTextMessageAsync(
                    update.Message.Chat.Id,
                    "⛔ This command is only available to administrators.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        await HandleCommandAsync(update.Message, cancellationToken);
    }

    protected abstract Task HandleCommandAsync(Message message, CancellationToken cancellationToken);

    private async Task<bool> IsAuthorizedAsync(long? userId, string? username, long chatId, CancellationToken cancellationToken)
    {
        if (_botOptions is null)
        {
            return true; // No admin configuration, allow all
        }

        // Check configured admins first
        if (_botOptions.AdminUserIds.Length > 0 || _botOptions.AdminUsernames.Length > 0)
        {
            // Check by user ID (more reliable)
            if (userId.HasValue && _botOptions.AdminUserIds.Contains(userId.Value))
            {
                return true;
            }

            // Fallback to username check
            if (!string.IsNullOrEmpty(username) && _botOptions.AdminUsernames.Contains(username, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check if user is a Telegram group admin/owner
        if (userId.HasValue)
        {
            var isTelegramAdmin = await IsTelegramGroupAdminAsync(chatId, userId.Value, cancellationToken);
            if (isTelegramAdmin)
            {
                return true;
            }
        }

        // If no configured admins and not a Telegram admin, allow all
        if (_botOptions.AdminUserIds.Length == 0 && _botOptions.AdminUsernames.Length == 0)
        {
            return true;
        }

        return false;
    }

    private async Task<bool> IsTelegramGroupAdminAsync(long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var chatMember = await Client.GetChatMemberAsync(chatId, userId, cancellationToken);

            // Check if user is creator (owner) or administrator
            return chatMember.Status == global::Telegram.Bot.Types.Enums.ChatMemberStatus.Creator ||
                   chatMember.Status == global::Telegram.Bot.Types.Enums.ChatMemberStatus.Administrator;
        }
        catch (Exception)
        {
            // If we can't check (e.g., bot doesn't have permission), default to false
            // This way configured admins will still work even if the bot can't check group admins
            return false;
        }
    }
}

