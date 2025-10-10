using Microsoft.Extensions.Logging;
using StrongLink.Worker.Services;
using StrongLink.Worker.Telegram.Updates;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram;

public sealed class UpdateDispatcher
{
    private readonly IEnumerable<IUpdateHandler> _handlers;
    private readonly ILogger<UpdateDispatcher> _logger;

    public UpdateDispatcher(
        IEnumerable<IUpdateHandler> handlers,
        ILogger<UpdateDispatcher> logger)
    {
        _handlers = handlers;
        _logger = logger;
    }

    public async Task DispatchAsync(Update update, CancellationToken cancellationToken)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(update))
            {
                _logger.LogDebug("Dispatching update {Type} to {Handler}", update.Type, handler.GetType().Name);
                await handler.HandleAsync(update, cancellationToken);
                return;
            }
        }

        _logger.LogDebug("No handler found for update type {Type}", update.Type);
    }
}

