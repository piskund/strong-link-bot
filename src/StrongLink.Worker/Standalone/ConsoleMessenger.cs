using StrongLink.Worker.Services;

namespace StrongLink.Worker.Standalone;

public sealed class ConsoleMessenger : IChatMessenger
{
    public Task<int> SendAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Chat {chatId}] {message}");
        return Task.FromResult(0); // Return dummy message ID for console mode
    }

    public Task<int> SendPhotoAsync(long chatId, string photoUrl, string caption, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Chat {chatId}] [Photo: {photoUrl}] {caption}");
        return Task.FromResult(0); // Return dummy message ID for console mode
    }
}

