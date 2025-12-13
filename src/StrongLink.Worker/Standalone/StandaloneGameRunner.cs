using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Standalone;

    public sealed class StandaloneGameRunner
{
    private const long HumanPlayerId = 1; // ID for the human player
    
    private readonly IGameLifecycleService _lifecycle;
    private readonly IGameSessionRepository _repository;
    private readonly ILocalizationService _localization;
    private readonly QuestionProviderFactory _questionProviderFactory;
    private readonly BotOptions _botOptions;
    private readonly GameOptions _gameOptions;
    private readonly DummyPlayerOptions _dummyOptions;
    private readonly StandaloneOptions _standaloneOptions;
    private readonly ILogger<StandaloneGameRunner> _logger;

        public StandaloneGameRunner(
        IGameLifecycleService lifecycle,
        IGameSessionRepository repository,
        ILocalizationService localization,
        QuestionProviderFactory questionProviderFactory,
        BotOptions botOptions,
        GameOptions gameOptions,
        DummyPlayerOptions dummyOptions,
        StandaloneOptions standaloneOptions,
        ILogger<StandaloneGameRunner> logger)
    {
        _lifecycle = lifecycle;
        _repository = repository;
        _localization = localization;
        _questionProviderFactory = questionProviderFactory;
        _botOptions = botOptions;
        _gameOptions = gameOptions;
        _dummyOptions = dummyOptions;
        _standaloneOptions = standaloneOptions;
        _logger = logger;
    }

        public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrintWelcome();
        var session = await PrepareSessionAsync(cancellationToken);
        
        if (session is null)
        {
            Console.WriteLine("Failed to prepare session. Check your configuration.");
            return;
        }
        
        await SimulateGameAsync(session, cancellationToken);
        ReportResults(session);
        ShowQuestionQualityReport(session);
        await ExportResultsAsync(session, cancellationToken);
    }
    
        private void PrintWelcome()
    {
        Console.WriteLine();
        Console.WriteLine("=====================================");
        Console.WriteLine("   Strong Link - Standalone Mode");
        Console.WriteLine("=====================================");
        Console.WriteLine($"Language: {_botOptions.DefaultLanguage}");
        Console.WriteLine($"Question Source: {_standaloneOptions.PreferredSource ?? _botOptions.QuestionSource}");
        Console.WriteLine($"Tours: {Math.Min(3, _gameOptions.Tours)}");
        Console.WriteLine($"Rounds per tour: {Math.Min(4, _gameOptions.RoundsPerTour)}");
        Console.WriteLine($"Dummy accuracy: {_dummyOptions.CorrectAnswerProbability:P0}");
        Console.WriteLine("=====================================");
        Console.WriteLine();
    }

        public StandaloneRunOptions? Options { get; set; }

        private async Task<GameSession?> PrepareSessionAsync(CancellationToken cancellationToken)
    {
        var language = Options?.Language ?? _botOptions.DefaultLanguage;
        var questionSource = Options?.Source ?? _standaloneOptions.PreferredSource ?? _botOptions.QuestionSource;

        var topics = (Options?.Topics?.Length > 0 ? Options.Topics : _gameOptions.Topics)?.ToArray();
        topics = (topics is { Length: > 0 } ? topics.Take(Math.Min(Options?.Tours ?? 3, topics.Length)).ToArray() : new[] { "General" });

        var session = new GameSession
        {
            ChatId = 0,
            Language = language,
            QuestionSourceMode = questionSource,
            Topics = topics,
            Tours = Math.Min(Options?.Tours ?? 3, _gameOptions.Tours),
            RoundsPerTour = Math.Min(Options?.Rounds ?? 4, _gameOptions.RoundsPerTour),
            AnswerTimeoutSeconds = Options?.TimeLimitSeconds ?? _gameOptions.AnswerTimeoutSeconds,
            EliminateLowest = Math.Min(1, _gameOptions.EliminateLowest)
        };

        // Add human and dummy players
        session.Players.AddRange(new[]
        {
            new Player { Id = HumanPlayerId, DisplayName = _standaloneOptions.HumanName, Status = PlayerStatus.Active },
            new Player { Id = 2, DisplayName = "Bot Alice", Status = PlayerStatus.Active },
            new Player { Id = 3, DisplayName = "Bot Bob", Status = PlayerStatus.Active },
            new Player { Id = 4, DisplayName = "Bot Charlie", Status = PlayerStatus.Active }
        });

        if (Options?.Players is int totalPlayers && totalPlayers > 1)
        {
            // retain 1 human, adjust number of dummies
            var desiredDummies = Math.Min(Math.Max(0, totalPlayers - 1), 7);
            while (session.Players.Count - 1 < desiredDummies)
            {
                var id = session.Players.Max(p => p.Id) + 1;
                session.Players.Add(new Player { Id = id, DisplayName = $"Bot {id}", Status = PlayerStatus.Active });
            }
            while (session.Players.Count - 1 > desiredDummies)
            {
                var last = session.Players.Last();
                if (last.Id != HumanPlayerId)
                {
                    session.Players.RemoveAt(session.Players.Count - 1);
                }
                else break;
            }
        }

        Console.WriteLine("Preparing questions...");
        
        try
        {
            var provider = _questionProviderFactory.Resolve(questionSource);
            IReadOnlyDictionary<int, List<Question>> pools;
            if (questionSource == QuestionSourceMode.Json)
            {
                var jsonProvider = provider as JsonQuestionProvider ?? throw new InvalidOperationException("JSON provider not registered");
                if (string.IsNullOrWhiteSpace(Options?.PoolFile))
                {
                    throw new InvalidOperationException("--pool-file is required when --source=json");
                }
                pools = await jsonProvider.PrepareFromFileAsync(
                    Options.PoolFile!,
                    session.Topics,
                    session.Tours,
                    session.RoundsPerTour,
                    session.Players,
                    cancellationToken);
            }
            else
            {
                pools = await provider.PrepareQuestionPoolAsync(
                    session.Topics,
                    session.Tours,
                    session.RoundsPerTour,
                    session.Players,
                    session.Language,
                    cancellationToken);
            }

            foreach (var (tour, questions) in pools)
            {
                var qList = questions;
                if (Options?.Shuffle is true)
                {
                    qList = qList.OrderBy(_ => Guid.NewGuid()).ToList();
                }
                session.QuestionsByTour[tour] = new Queue<Question>(qList);
            }
            
            Console.WriteLine($"✓ Loaded {pools.Sum(p => p.Value.Count)} questions for {session.Tours} tours");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare questions");
            Console.WriteLine($"Error loading questions: {ex.Message}");
            return null;
        }

        await _repository.SaveAsync(session, cancellationToken);
        if (Options?.DryRun is true)
        {
            Console.WriteLine("Dry run: pool prepared and validated. No gameplay.");
            session.Status = GameStatus.Completed;
            return session;
        }
        await _lifecycle.StartGameAsync(session, cancellationToken);
        return session;
    }

        private async Task SimulateGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        var random = Options?.Seed is int seed ? new Random(seed) : new Random();
        var botAccuracy = Options?.DummyAccuracyOverride ?? _dummyOptions.CorrectAnswerProbability;
        var questionsAnswered = new List<(Question question, string playerName, string playerAnswer, bool isCorrect)>();

        while (session.Status == GameStatus.InProgress || session.Status == GameStatus.SuddenDeath)
        {
            if (session.CurrentQuestion is null || session.CurrentPlayerId is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var currentPlayer = session.FindPlayer(session.CurrentPlayerId.Value);
            if (currentPlayer is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            string answer;
            bool isCorrect;
            
            if (session.CurrentPlayerId.Value == HumanPlayerId)
            {
                // Human player's turn - show question clearly
                Console.WriteLine();
                Console.WriteLine($"━━━━━━━━━ YOUR TURN ━━━━━━━━━");
                Console.WriteLine($"Tour {session.CurrentTour}, Round {session.CurrentRound + 1}");
                Console.WriteLine($"Topic: {session.CurrentQuestion.Topic}");
                Console.WriteLine();
                Console.WriteLine($"QUESTION: {session.CurrentQuestion.Text}");
                Console.WriteLine();
                Console.Write("Your answer: ");
                answer = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(session.AnswerTimeoutSeconds), cancellationToken) ?? string.Empty;
                
                // Check if answer is correct
                var normalizedAnswer = answer.Trim().ToLowerInvariant();
                var normalizedCorrect = session.CurrentQuestion.Answer.Trim().ToLowerInvariant();
                if (Options?.StrictMatch is true)
                {
                    isCorrect = normalizedAnswer == normalizedCorrect;
                }
                else
                {
                    // simple fuzzy: contains or startswith
                    isCorrect = normalizedAnswer == normalizedCorrect
                        || (!string.IsNullOrWhiteSpace(normalizedAnswer) && normalizedCorrect.Contains(normalizedAnswer, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(normalizedAnswer) && normalizedAnswer.Contains(normalizedCorrect, StringComparison.OrdinalIgnoreCase));
                }
                
                if (isCorrect)
                {
                    Console.WriteLine("✓ Correct!");
                }
                else
                {
                    if (Options?.ShowAnswers is not false)
                    {
                        Console.WriteLine($"✗ Wrong! The correct answer was: {session.CurrentQuestion.Answer}");
                    }
                }
            }
            else
            {
                // Bot player's turn
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                
                var shouldAnswerCorrectly = random.NextDouble() < botAccuracy;
                answer = shouldAnswerCorrectly ? session.CurrentQuestion.Answer : $"Wrong answer {random.Next(1000)}";
                isCorrect = shouldAnswerCorrectly;
                
                Console.WriteLine($"\n{currentPlayer.DisplayName} answers: {answer}");
                Console.WriteLine(isCorrect ? "✓ Correct!" : $"✗ Wrong! (Answer: {session.CurrentQuestion.Answer})");
            }
            
            // Store question for quality review
            questionsAnswered.Add((session.CurrentQuestion, currentPlayer.DisplayName, answer, isCorrect));

            await _lifecycle.HandleAnswerAsync(session, session.CurrentPlayerId.Value, answer, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
        
        // Store for quality report
        session.Metadata["QuestionsAnswered"] = questionsAnswered;
    }

    private void ReportResults(GameSession session)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════ GAME RESULTS ════════════════");
        Console.WriteLine();
        Console.WriteLine("Final Standings:");
        Console.WriteLine("────────────────────────────────────────────");
        
        var rank = 1;
        foreach (var player in session.Players.OrderByDescending(p => p.Score).ThenBy(p => p.IncorrectAnswers))
        {
            var status = player.Status == PlayerStatus.Active ? "🏆" : "❌";
            Console.WriteLine($" {rank,2}. {player.DisplayName,-15} | Score: {player.Score,2} | Correct: {player.CorrectAnswers,2} | Wrong: {player.IncorrectAnswers,2} | {status}");
            rank++;
        }
        Console.WriteLine("════════════════════════════════════════════════");
    }
    
        private void ShowQuestionQualityReport(GameSession session)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════ QUESTION QUALITY REPORT ════════════════");
        Console.WriteLine();
        Console.WriteLine($"Question Source: {session.QuestionSourceMode}");
        Console.WriteLine($"Language: {session.Language}");
        Console.WriteLine();
        
        // Show all questions that were asked
        if (session.Metadata.TryGetValue("QuestionsAnswered", out var questionsObj) && 
            questionsObj is List<(Question question, string playerName, string playerAnswer, bool isCorrect)> questions)
        {
            Console.WriteLine("Questions Asked During Game:");
            Console.WriteLine("────────────────────────────────────────────────────────");
            
            var tourGroups = questions.GroupBy(q => q.question.Topic);
            foreach (var tourGroup in tourGroups)
            {
                Console.WriteLine($"\nTopic: {tourGroup.Key}");
                Console.WriteLine(new string('─', 50));
                
                var qNum = 1;
                foreach (var (question, _, _, _) in tourGroup)
                {
                    Console.WriteLine($"\n  Q{qNum}: {question.Text}");
                    Console.WriteLine($"  A: {question.Answer}");
                    qNum++;
                }
            }
        }
        
        // Show remaining questions in the pool (not asked)
        Console.WriteLine();
        Console.WriteLine("Remaining Questions in Pool:");
        Console.WriteLine("────────────────────────────────────────────────────────");
        
        foreach (var (tour, questionQueue) in session.QuestionsByTour.OrderBy(kvp => kvp.Key))
        {
            if (questionQueue.Count > 0)
            {
                var topic = session.Topics.ElementAtOrDefault(tour - 1) ?? $"Tour {tour}";
                Console.WriteLine($"\nTour {tour} ({topic}): {questionQueue.Count} questions remaining");
                
                var remainingQuestions = questionQueue.ToList();
                for (var i = 0; i < Math.Min(3, remainingQuestions.Count); i++)
                {
                    var q = remainingQuestions[i];
                    Console.WriteLine($"  • {q.Text}");
                    Console.WriteLine($"    Answer: {q.Answer}");
                }
                
                if (remainingQuestions.Count > 3)
                {
                    Console.WriteLine($"  ... and {remainingQuestions.Count - 3} more");
                }
            }
        }
        
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine("Use this report to evaluate question quality and difficulty.");
        Console.WriteLine("You can adjust settings in appsettings.json or CLI to test different configurations.");
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var readTask = Task.Run(Console.ReadLine, cancellationToken);
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout, cancellationToken));
            if (completed == readTask)
            {
                return readTask.Result;
            }
            Console.WriteLine();
            Console.WriteLine("(timeout)");
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task ExportResultsAsync(GameSession session, CancellationToken cancellationToken)
    {
        try
        {
            string path;
            if (!string.IsNullOrWhiteSpace(Options?.ExportPath))
            {
                path = Options.ExportPath!;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }
            else
            {
                Directory.CreateDirectory(_botOptions.ResultsStoragePath);
                path = Path.Combine(_botOptions.ResultsStoragePath, $"standalone_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            }

            var attempts = new List<ExportAttempt>();
            if (session.Metadata.TryGetValue("QuestionsAnswered", out var obj) && obj is List<(Question question, string playerName, string playerAnswer, bool isCorrect)> list)
            {
                foreach (var (q, playerName, playerAnswer, isCorrect) in list)
                {
                    attempts.Add(new ExportAttempt
                    {
                        Tour = session.Topics.ToList().FindIndex(t => string.Equals(t, q.Topic, StringComparison.OrdinalIgnoreCase)) + 1,
                        Topic = q.Topic,
                        Player = playerName,
                        Question = q.Text,
                        CorrectAnswer = q.Answer,
                        GivenAnswer = playerAnswer,
                        IsCorrect = isCorrect
                    });
                }
            }

            var export = new Export
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Language = session.Language.ToString(),
                Source = session.QuestionSourceMode.ToString(),
                Topics = session.Topics,
                Tours = session.Tours,
                RoundsPerTour = session.RoundsPerTour,
                Players = session.Players.Select(p => new ExportPlayer { Name = p.DisplayName, Score = p.Score, Correct = p.CorrectAnswers, Wrong = p.IncorrectAnswers }).ToList(),
                Attempts = attempts
            };

            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            await File.WriteAllTextAsync(path, json, cancellationToken);
            Console.WriteLine($"Exported results to: {path}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export results");
        }
    }

    private sealed class Export
    {
        public required DateTimeOffset GeneratedAt { get; init; }
        public required string Language { get; init; }
        public required string Source { get; init; }
        public required IEnumerable<string> Topics { get; init; }
        public required int Tours { get; init; }
        public required int RoundsPerTour { get; init; }
        public required IEnumerable<ExportPlayer> Players { get; init; }
        public required IEnumerable<ExportAttempt> Attempts { get; init; }
    }

    private sealed class ExportAttempt
    {
        public int Tour { get; init; }
        public required string Topic { get; init; }
        public required string Player { get; init; }
        public required string Question { get; init; }
        public required string CorrectAnswer { get; init; }
        public required string GivenAnswer { get; init; }
        public required bool IsCorrect { get; init; }
    }

    private sealed class ExportPlayer
    {
        public required string Name { get; init; }
        public int Score { get; init; }
        public int Correct { get; init; }
        public int Wrong { get; init; }
    }
}

