using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Telegram;

public sealed class TelegramBotService : IBotLifetimeService
{
    private readonly ITelegramBotClient _client;
    private readonly BotOptions _botOptions;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public TelegramBotService(
        ITelegramBotClient client,
        IOptions<BotOptions> botOptions,
        ILogger<TelegramBotService> logger,
        IServiceProvider serviceProvider)
    {
        _client = client;
        _botOptions = botOptions.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var me = await _client.GetMeAsync(cancellationToken);
        _logger.LogInformation("Starting Strong Link bot as @{Username}", me.Username);

        if (_botOptions.Polling.UseWebhook)
        {
            _logger.LogWarning("Webhook mode configured but not implemented. Falling back to polling.");
        }

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[]
            {
                UpdateType.Message,
                UpdateType.EditedMessage,
                UpdateType.CallbackQuery
            }
        };

        _client.StartReceiving(new DefaultUpdateHandler(HandleUpdateAsync, HandleErrorAsync), receiverOptions, cancellationToken);
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Strong Link bot");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<UpdateDispatcher>();
        await dispatcher.DispatchAsync(update, cancellationToken);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException =>
                $"Telegram API Error:\n[{apiRequestException.ErrorCode}] {apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(errorMessage);
        return Task.CompletedTask;
    }
}

