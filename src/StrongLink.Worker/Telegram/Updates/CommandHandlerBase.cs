using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates;

public abstract class CommandHandlerBase : IUpdateHandler
{
    protected CommandHandlerBase(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository)
    {
        Client = client;
        Localization = localization;
        Repository = repository;
    }

    protected ITelegramBotClient Client { get; }

    protected ILocalizationService Localization { get; }

    protected IGameSessionRepository Repository { get; }

    public abstract string Command { get; }

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

        await HandleCommandAsync(update.Message, cancellationToken);
    }

    protected abstract Task HandleCommandAsync(Message message, CancellationToken cancellationToken);
}

