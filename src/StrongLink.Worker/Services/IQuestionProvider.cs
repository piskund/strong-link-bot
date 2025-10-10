using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

public interface IQuestionProvider
{
    QuestionSourceMode Mode { get; }

    Task<IReadOnlyDictionary<int, List<Question>>> PrepareQuestionPoolAsync(
        IReadOnlyList<string> topics,
        int tours,
        int roundsPerTour,
        IReadOnlyList<Player> players,
        GameLanguage language,
        CancellationToken cancellationToken);
}

