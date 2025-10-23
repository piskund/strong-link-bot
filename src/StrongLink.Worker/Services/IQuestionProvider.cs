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

    Task<IReadOnlyList<Question>> PrepareStandaloneQuestionsAsync(
        IReadOnlyList<string> topics,
        int tours,
        int roundsPerTour,
        GameLanguage language,
        CancellationToken cancellationToken) =>
        PrepareQuestionPoolAsync(topics, tours, roundsPerTour, Array.Empty<Player>(), language, cancellationToken)
            .ContinueWith(t => (IReadOnlyList<Question>)t.Result.SelectMany(pair => pair.Value).ToList(), cancellationToken);
}

