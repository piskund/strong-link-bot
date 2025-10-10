using Microsoft.Extensions.Logging;
using StrongLink.Worker.Services;
using Telegram.Bot;

namespace StrongLink.Worker.Telegram;

public sealed class ChatMessenger : IChatMessenger
{
    private readonly ITelegramBotClient _client;
    private readonly ILogger<ChatMessenger> _logger;

    public ChatMessenger(ITelegramBotClient client, ILogger<ChatMessenger> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task SendAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to chat {ChatId}", chatId);
            throw;
        }
    }
}

