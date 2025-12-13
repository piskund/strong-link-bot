using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker;

public static class TestQuestionGeneration
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Testing Question Generation ===\n");

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
            Console.WriteLine($"Loaded .env from: {envPath}");
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENAI__APIKEY");
        Console.WriteLine($"OpenAI API Key present: {!string.IsNullOrEmpty(apiKey)}");
        Console.WriteLine($"OpenAI API Key length: {apiKey?.Length ?? 0}\n");

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: No OpenAI API key found!");
            return;
        }

        // Create logger
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        var logger = loggerFactory.CreateLogger<AiQuestionProvider>();

        // Create HTTP client
        var httpClient = new HttpClient();

        // Create options
        var options = Options.Create(new OpenAiOptions
        {
            ApiKey = apiKey,
            Model = "gpt-4o-mini",
            Endpoint = "https://api.openai.com/v1/chat/completions"
        });

        // Create localization service
        var localizationService = new LocalizationService();

        // Create provider
        var provider = new AiQuestionProvider(httpClient, options, localizationService, logger);

        Console.WriteLine("Created AiQuestionProvider\n");

        // Test question generation
        try
        {
            Console.WriteLine("Generating 3 questions for topic 'Geography' in English...\n");

            var result = await provider.PrepareQuestionPoolAsync(
                topics: new[] { "Geography" },
                tours: 1,
                roundsPerTour: 3,
                players: new List<Player> { new Player { Id = 1, DisplayName = "TestPlayer", Status = PlayerStatus.Active } },
                language: GameLanguage.English,
                cancellationToken: CancellationToken.None
            );

            Console.WriteLine($"\nResult dictionary has {result.Count} entries");

            foreach (var (tour, questions) in result)
            {
                Console.WriteLine($"\nTour {tour}: {questions.Count} questions");
                foreach (var q in questions)
                {
                    Console.WriteLine($"  Q: {q.Text}");
                    Console.WriteLine($"  A: {q.Answer}");
                    Console.WriteLine();
                }
            }

            if (result.Values.Sum(q => q.Count) == 0)
            {
                Console.WriteLine("ERROR: No questions were generated!");
            }
            else
            {
                Console.WriteLine("SUCCESS: Questions generated successfully!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
        }
    }
}
