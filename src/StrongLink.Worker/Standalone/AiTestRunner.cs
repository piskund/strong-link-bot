using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker.Standalone;

public sealed class AiTestRunner
{
    private readonly AiQuestionProvider _aiProvider;
    private readonly BotOptions _botOptions;
    private readonly GameOptions _gameOptions;
    private readonly ILogger<AiTestRunner> _logger;

    public AiTestRunner(
        AiQuestionProvider aiProvider,
        BotOptions botOptions,
        GameOptions gameOptions,
        ILogger<AiTestRunner> logger)
    {
        _aiProvider = aiProvider;
        _botOptions = botOptions;
        _gameOptions = gameOptions;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var language = _botOptions.DefaultLanguage;
        var topics = _gameOptions.Topics.Length > 0 ? _gameOptions.Topics : new[] { "General" };
        var tours = Math.Min(3, _gameOptions.Tours);
        var roundsPerTour = Math.Min(4, _gameOptions.RoundsPerTour);

        Console.WriteLine();
        Console.WriteLine("=====================================");
        Console.WriteLine("   Strong Link - AI Question Test");
        Console.WriteLine("=====================================");
        Console.WriteLine($"Language: {language}");
        Console.WriteLine($"Model: (see OpenAi model in config)");
        Console.WriteLine($"Topics: {string.Join(", ", topics)}");
        Console.WriteLine($"Tours: {tours}  Rounds: {roundsPerTour}");
        Console.WriteLine("=====================================");
        Console.WriteLine();

        // Prepare a single-player context (no impact on generation format)
        var players = new List<Player> { new() { Id = 1, DisplayName = "Tester", Status = PlayerStatus.Active } };

        var pools = await _aiProvider.PrepareQuestionPoolAsync(
            topics,
            tours,
            roundsPerTour,
            players,
            language,
            cancellationToken);

        // Flatten into an ordered list by tour
        var ordered = pools.OrderBy(k => k.Key)
            .SelectMany(k => k.Value.Select((q, i) => new { Tour = k.Key, Index = i + 1, Question = q }))
            .ToList();

        var attempts = new List<Attempt>();
        foreach (var item in ordered)
        {
            Console.WriteLine();
            Console.WriteLine($"Tour {item.Tour} | Topic: {item.Question.Topic} | Q{item.Index}");
            Console.WriteLine($"QUESTION: {item.Question.Text}");
            Console.Write("Your answer: ");
            var answer = Console.ReadLine() ?? string.Empty;
            var ok = string.Equals(answer.Trim(), item.Question.Answer.Trim(), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(ok ? "✓ Correct" : $"✗ Wrong (Answer: {item.Question.Answer})");
            attempts.Add(new Attempt(item.Tour, item.Question.Topic, item.Question.Text, item.Question.Answer, answer, ok));
        }

        // Summary
        var correct = attempts.Count(a => a.IsCorrect);
        Console.WriteLine();
        Console.WriteLine($"Result: {correct}/{attempts.Count} correct");

        // Export
        Directory.CreateDirectory(_botOptions.ResultsStoragePath);
        var export = new Export
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Language = language.ToString(),
            Topics = topics,
            Tours = tours,
            RoundsPerTour = roundsPerTour,
            Attempts = attempts
        };

        var file = Path.Combine(_botOptions.ResultsStoragePath, $"ai_test_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }), cancellationToken);

        Console.WriteLine($"Exported report to: {file}");
    }

    private sealed record Attempt(int Tour, string Topic, string Question, string CorrectAnswer, string YourAnswer, bool IsCorrect);

    private sealed record Export
    {
        public required DateTimeOffset GeneratedAt { get; init; }
        public required string Language { get; init; }
        public required IEnumerable<string> Topics { get; init; }
        public required int Tours { get; init; }
        public required int RoundsPerTour { get; init; }
        public required IEnumerable<Attempt> Attempts { get; init; }
    }
}

