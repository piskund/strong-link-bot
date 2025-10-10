using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Standalone;

public sealed class StandaloneGameRunner
{
    private readonly IGameLifecycleService _lifecycle;
    private readonly IGameSessionRepository _repository;
    private readonly ILocalizationService _localization;
    private readonly GameOptions _gameOptions;
    private readonly DummyPlayerOptions _dummyOptions;

    public StandaloneGameRunner(
        IGameLifecycleService lifecycle,
        IGameSessionRepository repository,
        ILocalizationService localization,
        GameOptions gameOptions,
        DummyPlayerOptions dummyOptions)
    {
        _lifecycle = lifecycle;
        _repository = repository;
        _localization = localization;
        _gameOptions = gameOptions;
        _dummyOptions = dummyOptions;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var session = await PrepareSessionAsync(cancellationToken);
        await SimulateGameAsync(session, cancellationToken);
        ReportResults(session);
    }

    private async Task<GameSession> PrepareSessionAsync(CancellationToken cancellationToken)
    {
        var session = new GameSession
        {
            ChatId = 0,
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = _gameOptions.Topics.Length > 0 ? _gameOptions.Topics : new[] { "General" },
            Tours = Math.Min(3, _gameOptions.Tours),
            RoundsPerTour = Math.Min(4, _gameOptions.RoundsPerTour),
            AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
            EliminateLowest = Math.Min(1, _gameOptions.EliminateLowest)
        };

        session.Players.AddRange(new[]
        {
            new Player { Id = 1, DisplayName = "Alice", Status = PlayerStatus.Active },
            new Player { Id = 2, DisplayName = "Bob", Status = PlayerStatus.Active },
            new Player { Id = 3, DisplayName = "Charlie", Status = PlayerStatus.Active }
        });

        foreach (var (topic, index) in session.Topics.Select((topic, index) => (topic, index + 1)))
        {
            var queue = new Queue<Question>();
            for (var round = 1; round <= session.RoundsPerTour; round++)
            {
                queue.Enqueue(new Question
                {
                    Topic = topic,
                    Text = $"{topic} question {round}?",
                    Answer = "Answer"
                });
            }

            session.QuestionsByTour[index] = queue;
        }

        await _repository.SaveAsync(session, cancellationToken);
        await _lifecycle.StartGameAsync(session, cancellationToken);
        return session;
    }

    private async Task SimulateGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        var random = new Random();

        while (session.Status == GameStatus.InProgress)
        {
            if (session.CurrentQuestion is null || session.CurrentPlayerId is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var answerCorrect = random.NextDouble() < _dummyOptions.CorrectAnswerProbability;
            var answer = answerCorrect ? session.CurrentQuestion.Answer : "Incorrect Guess";
            await _lifecycle.HandleAnswerAsync(session, session.CurrentPlayerId.Value, answer, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private void ReportResults(GameSession session)
    {
        Console.WriteLine();
        Console.WriteLine("================ Strong Link Standalone Summary ================");
        foreach (var player in session.Players.OrderByDescending(p => p.Score))
        {
            Console.WriteLine($"{player.DisplayName,-10} | Score: {player.Score,2} | Status: {player.Status}");
        }
        Console.WriteLine("================================================================");
    }
}

