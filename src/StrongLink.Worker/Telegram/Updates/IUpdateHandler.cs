using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates;

public interface IUpdateHandler
{
    bool CanHandle(Update update);

    Task HandleAsync(Update update, CancellationToken cancellationToken);
}

