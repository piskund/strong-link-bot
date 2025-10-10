using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Persistence;

public sealed class InMemoryGameSessionRepository : IGameSessionRepository
{
    private readonly Dictionary<long, GameSession> _store = new();

    public Task SaveAsync(GameSession session, CancellationToken cancellationToken)
    {
        _store[session.ChatId] = session;
        return Task.CompletedTask;
    }

    public Task<GameSession?> LoadAsync(long chatId, CancellationToken cancellationToken)
    {
        _store.TryGetValue(chatId, out var session);
        return Task.FromResult(session);
    }

    public Task RemoveAsync(long chatId, CancellationToken cancellationToken)
    {
        _store.Remove(chatId);
        return Task.CompletedTask;
    }
}

