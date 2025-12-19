namespace StrongLink.Worker.Services;

public interface IChatMessenger
{
    Task<int> SendAsync(long chatId, string message, CancellationToken cancellationToken);
}

