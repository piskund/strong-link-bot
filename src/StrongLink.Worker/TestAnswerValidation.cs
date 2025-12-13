using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker;

public static class TestAnswerValidation
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Testing AI Answer Validation ===\n");

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
        Console.WriteLine($"OpenAI API Key present: {!string.IsNullOrEmpty(apiKey)}\n");

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
        var logger = loggerFactory.CreateLogger<AiAnswerValidator>();

        // Create HTTP client
        var httpClient = new HttpClient();

        // Create options
        var options = Options.Create(new OpenAiOptions
        {
            ApiKey = apiKey,
            Model = "gpt-4o-mini",
            Endpoint = "https://api.openai.com/v1/chat/completions"
        });

        // Create validator
        var validator = new AiAnswerValidator(httpClient, options, logger);

        Console.WriteLine("Testing answer validation with various cases...\n");

        // Test cases
        var testCases = new[]
        {
            new
            {
                Question = "Какой автор создал детектива Шерлока Холмса?",
                CorrectAnswer = "Артур Конан Дойл",
                UserAnswer = "Артур Конан Дойль",
                Language = GameLanguage.Russian,
                ExpectedResult = true,
                Description = "Russian name with minor spelling difference (ь vs л)"
            },
            new
            {
                Question = "What is the capital of France?",
                CorrectAnswer = "Paris",
                UserAnswer = "paris",
                Language = GameLanguage.English,
                ExpectedResult = true,
                Description = "Case difference"
            },
            new
            {
                Question = "What is the largest ocean?",
                CorrectAnswer = "Pacific Ocean",
                UserAnswer = "Pacific",
                Language = GameLanguage.English,
                ExpectedResult = true,
                Description = "Missing 'Ocean' suffix"
            },
            new
            {
                Question = "Who wrote War and Peace?",
                CorrectAnswer = "Leo Tolstoy",
                UserAnswer = "Lev Tolstoy",
                Language = GameLanguage.English,
                ExpectedResult = true,
                Description = "Name variant (Leo vs Lev)"
            },
            new
            {
                Question = "What is 2 + 2?",
                CorrectAnswer = "4",
                UserAnswer = "four",
                Language = GameLanguage.English,
                ExpectedResult = true,
                Description = "Number as word"
            },
            new
            {
                Question = "What is the capital of France?",
                CorrectAnswer = "Paris",
                UserAnswer = "London",
                Language = GameLanguage.English,
                ExpectedResult = false,
                Description = "Wrong answer"
            }
        };

        int passed = 0;
        int failed = 0;

        foreach (var testCase in testCases)
        {
            Console.WriteLine($"Test: {testCase.Description}");
            Console.WriteLine($"  Question: {testCase.Question}");
            Console.WriteLine($"  Expected: {testCase.CorrectAnswer}");
            Console.WriteLine($"  User answered: {testCase.UserAnswer}");

            try
            {
                var result = await validator.ValidateAnswerAsync(
                    testCase.UserAnswer,
                    testCase.CorrectAnswer,
                    testCase.Question,
                    testCase.Language,
                    CancellationToken.None);

                var status = result == testCase.ExpectedResult ? "✓ PASS" : "✗ FAIL";
                Console.WriteLine($"  Result: {result} - {status}");

                if (result == testCase.ExpectedResult)
                {
                    passed++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  Expected {testCase.ExpectedResult} but got {result}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
                failed++;
            }

            Console.WriteLine();
        }

        Console.WriteLine($"\n=== Results ===");
        Console.WriteLine($"Passed: {passed}/{testCases.Length}");
        Console.WriteLine($"Failed: {failed}/{testCases.Length}");

        if (failed == 0)
        {
            Console.WriteLine("\nSUCCESS: All tests passed!");
        }
        else
        {
            Console.WriteLine("\nWARNING: Some tests failed.");
        }
    }
}
