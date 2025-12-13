using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Persistence;

public interface IGameResultRepository
{
    /// <summary>
    /// Archives a completed game result for historical record-keeping.
    /// </summary>
    Task ArchiveAsync(GameResult result, CancellationToken cancellationToken);
}
