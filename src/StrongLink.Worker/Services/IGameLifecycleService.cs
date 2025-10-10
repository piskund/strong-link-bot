using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

public interface IGameLifecycleService
{
    Task StartGameAsync(GameSession session, CancellationToken cancellationToken);

    Task AdvanceRoundAsync(GameSession session, CancellationToken cancellationToken);

    Task HandleAnswerAsync(GameSession session, long playerId, string answer, CancellationToken cancellationToken);

    Task StopGameAsync(GameSession session, CancellationToken cancellationToken);
}

