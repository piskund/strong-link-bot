using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

public interface IGameSessionRepository
{
    Task SaveAsync(GameSession session, CancellationToken cancellationToken);

    Task<GameSession?> LoadAsync(long chatId, CancellationToken cancellationToken);

    Task RemoveAsync(long chatId, CancellationToken cancellationToken);
}

