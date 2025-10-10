namespace StrongLink.Worker.Services;

public interface IChatMessenger
{
    Task SendAsync(long chatId, string message, CancellationToken cancellationToken);
}

