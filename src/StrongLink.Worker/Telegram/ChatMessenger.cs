using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

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

    public async Task<int> SendAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        try
        {
            var sentMessage = await _client.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
            return sentMessage.MessageId;
        }
        catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 403)
        {
            // Bot was blocked by user or kicked from group - log and don't retry
            _logger.LogWarning("Cannot send message to chat {ChatId}: Bot was blocked or removed (403 Forbidden)", chatId);
            return -1; // Return invalid message ID to indicate failure
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to chat {ChatId}", chatId);
            throw;
        }
    }

    public async Task<int> SendPhotoAsync(long chatId, string photoUrl, string caption, CancellationToken cancellationToken)
    {
        try
        {
            // In Telegram.Bot v18, SendPhotoAsync accepts a string URL directly
            var sentMessage = await _client.SendPhotoAsync(
                chatId,
                photo: photoUrl,
                caption: caption,
                cancellationToken: cancellationToken);
            return sentMessage.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send photo to chat {ChatId} with URL {PhotoUrl}, attempting fallback strategies", chatId, photoUrl);

            // Retry strategy 1: If it's a Wikimedia URL with a specific size, try smaller sizes
            if (IsWikimediaThumbUrl(photoUrl))
            {
                var result = await TrySmallSizesAsync(chatId, photoUrl, caption, cancellationToken);
                if (result.HasValue)
                    return result.Value;
            }

            // Retry strategy 2: Pre-download and upload as file
            var fallbackResult = await TryPreDownloadAsync(chatId, photoUrl, caption, cancellationToken);
            if (fallbackResult.HasValue)
                return fallbackResult.Value;

            // All strategies failed
            _logger.LogError(ex, "All image sending strategies failed for chat {ChatId} with URL {PhotoUrl}", chatId, photoUrl);
            throw;
        }
    }

    private static bool IsWikimediaThumbUrl(string url)
    {
        return url.Contains("upload.wikimedia.org") &&
               url.Contains("/thumb/") &&
               System.Text.RegularExpressions.Regex.IsMatch(url, @"\d+px-");
    }

    private async Task<int?> TrySmallSizesAsync(long chatId, string photoUrl, string caption, CancellationToken cancellationToken)
    {
        var sizeMatch = System.Text.RegularExpressions.Regex.Match(photoUrl, @"(\d+)px-");
        if (!sizeMatch.Success)
            return null;

        var currentSize = int.Parse(sizeMatch.Groups[1].Value);
        var smallerSizes = new[] { 640, 400, 320 };

        foreach (var targetSize in smallerSizes)
        {
            // Skip if we're already at this size or smaller
            if (currentSize <= targetSize)
                continue;

            var smallerUrl = System.Text.RegularExpressions.Regex.Replace(
                photoUrl,
                @"\d+px-",
                $"{targetSize}px-");

            try
            {
                _logger.LogInformation("Retrying with smaller image size: {Size}px (original: {OriginalSize}px)",
                    targetSize, currentSize);

                var sentMessage = await _client.SendPhotoAsync(
                    chatId,
                    photo: smallerUrl,
                    caption: caption,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Successfully sent photo with smaller size {Size}px", targetSize);
                return sentMessage.MessageId;
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx, "Failed to send photo with size {Size}px", targetSize);
                // Continue to next size
            }
        }

        return null;
    }

    private async Task<int?> TryPreDownloadAsync(long chatId, string photoUrl, string caption, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting to pre-download image and upload as file");

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var imageBytes = await httpClient.GetByteArrayAsync(photoUrl, cancellationToken);

            var stream = new MemoryStream(imageBytes);
            var fileName = ExtractFileName(photoUrl) ?? "image.jpg";

            // In Telegram.Bot v18, SendPhotoAsync accepts Stream which is implicitly converted to InputOnlineFile
            var sentMessage = await _client.SendPhotoAsync(
                chatId,
                photo: stream!,
                caption: caption,
                cancellationToken: cancellationToken);

            stream.Dispose();

            _logger.LogInformation("Successfully sent photo via pre-download fallback");
            return sentMessage.MessageId;
        }
        catch (Exception downloadEx)
        {
            _logger.LogWarning(downloadEx, "Failed to send photo via pre-download fallback");
            return null;
        }
    }

    private static string? ExtractFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.Segments;
            return segments.Length > 0 ? segments[^1] : null;
        }
        catch
        {
            return null;
        }
    }
}

