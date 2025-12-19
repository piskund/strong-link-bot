using StrongLink.Worker.Services;

namespace StrongLink.Worker.Standalone;

public sealed class ConsoleMessenger : IChatMessenger
{
    public Task<int> SendAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Chat {chatId}] {message}");
        return Task.FromResult(0); // Return dummy message ID for console mode
    }
}

