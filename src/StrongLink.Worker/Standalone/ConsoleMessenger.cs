using StrongLink.Worker.Services;

namespace StrongLink.Worker.Standalone;

public sealed class ConsoleMessenger : IChatMessenger
{
    public Task SendAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Chat {chatId}] {message}");
        return Task.CompletedTask;
    }
}

