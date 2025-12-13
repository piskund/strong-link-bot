using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker;

public static class TestPrepareFlow
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Testing Full Prepare Pool Flow ===\n");

        // Load environment variables
        var currentDir = Directory.GetCurrentDirectory();
        string? envPath = null;
        while (currentDir != null)
        {
            var testPath = Path.Combine(currentDir, ".env");
            if (File.Exists(testPath))
            {
                envPath = testPath;
                break;
            }
            var parent = Directory.GetParent(currentDir);
            currentDir = parent?.FullName;
        }

        if (envPath != null)
        {
            DotNetEnv.Env.Load(envPath);
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENAI__APIKEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: No OpenAI API key found!");
            return;
        }

        // Create logger
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Create session (simulating what exists after /start and /join)
        var session = new GameSession
        {
            ChatId = -999999, // Test chat ID
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = new[] { "Geography", "History" },
            Tours = 2,
            RoundsPerTour = 3,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1,
            Status = GameStatus.AwaitingPlayers
        };

        // Add a player
        session.Players.Add(new Player
        {
            Id = 12345,
            DisplayName = "@testuser",
            Status = PlayerStatus.Active
        });

        Console.WriteLine($"Session setup:");
        Console.WriteLine($"  Tours: {session.Tours}");
        Console.WriteLine($"  Rounds per tour: {session.RoundsPerTour}");
        Console.WriteLine($"  Players: {session.Players.Count}");
        Console.WriteLine($"  Topics: {string.Join(", ", session.Topics)}\n");

        // === SIMULATE PREPARE POOL LOGIC ===
        try
        {
            var requiredPerTour = Math.Max(1, session.Players.Count) * session.RoundsPerTour;
            Console.WriteLine($"Required questions per tour: {requiredPerTour}\n");

            // Create question provider
            var httpClient = new HttpClient();
            var options = Options.Create(new OpenAiOptions
            {
                ApiKey = apiKey,
                Model = "gpt-4o-mini",
                Endpoint = "https://api.openai.com/v1/chat/completions"
            });
            var localizationService = new LocalizationService();
            var logger = loggerFactory.CreateLogger<AiQuestionProvider>();
            var provider = new AiQuestionProvider(httpClient, options, localizationService, logger);

            var pool = new Dictionary<int, List<Question>>();
            var generatedQuestions = new List<Question>();

            for (var tourIndex = 0; tourIndex < session.Tours; tourIndex++)
            {
                var topic = session.Topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
                Console.WriteLine($"Processing tour {tourIndex + 1}, topic: {topic}");

                // Generate questions
                var generated = await provider.PrepareQuestionPoolAsync(
                    new[] { topic },
                    1,
                    session.RoundsPerTour,
                    session.Players,
                    session.Language,
                    CancellationToken.None);

                Console.WriteLine($"  Provider returned {generated.Count} dictionary entries");

                var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                Console.WriteLine($"  Generated {generatedList.Count} questions");
                generatedQuestions.AddRange(generatedList);

                pool[tourIndex + 1] = generatedList.Take(requiredPerTour).ToList();
                Console.WriteLine($"  Added {pool[tourIndex + 1].Count} questions to pool\n");
            }

            Console.WriteLine($"Pool dictionary summary:");
            Console.WriteLine($"  Tours in pool: {pool.Count}");
            foreach (var (tour, questions) in pool)
            {
                Console.WriteLine($"  Tour {tour}: {questions.Count} questions");
            }

            // Copy to session
            session.QuestionsByTour.Clear();
            foreach (var (tour, questions) in pool)
            {
                session.QuestionsByTour[tour] = new Queue<Question>(questions);
            }

            var totalQuestions = session.QuestionsByTour.Values.Sum(q => q.Count);
            Console.WriteLine($"\nSession QuestionsByTour:");
            Console.WriteLine($"  Total questions: {totalQuestions}");
            foreach (var (tour, queue) in session.QuestionsByTour)
            {
                Console.WriteLine($"  Tour {tour}: {queue.Count} questions");
            }

            // Test serialization
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            var json = JsonSerializer.Serialize(session, jsonOptions);
            var deserialized = JsonSerializer.Deserialize<GameSession>(json, jsonOptions);

            Console.WriteLine($"\nAfter serialization/deserialization:");
            Console.WriteLine($"  Total questions: {deserialized?.QuestionsByTour.Values.Sum(q => q.Count) ?? 0}");

            if (deserialized?.QuestionsByTour.Values.Sum(q => q.Count) == totalQuestions && totalQuestions > 0)
            {
                Console.WriteLine("\nSUCCESS: Full flow works correctly!");
            }
            else
            {
                Console.WriteLine("\nERROR: Questions were lost!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
        }
    }
}
