using System.Text.Json;
using StrongLink.Worker.Domain;

namespace StrongLink.Worker;

public static class TestSerialization
{
    public static void Run()
    {
        Console.WriteLine("=== Testing Queue<Question> Serialization ===\n");

        var session = new GameSession
        {
            ChatId = 12345,
            Language = GameLanguage.English,
            QuestionSourceMode = QuestionSourceMode.AI,
            Topics = new[] { "Test" },
            Tours = 2,
            RoundsPerTour = 2,
            AnswerTimeoutSeconds = 30,
            EliminateLowest = 1
        };

        // Add questions to tour 1
        var questions1 = new Queue<Question>();
        questions1.Enqueue(new Question { Topic = "Geography", Text = "What is the capital of France?", Answer = "Paris" });
        questions1.Enqueue(new Question { Topic = "Geography", Text = "What is the largest ocean?", Answer = "Pacific" });
        session.QuestionsByTour[1] = questions1;

        // Add questions to tour 2
        var questions2 = new Queue<Question>();
        questions2.Enqueue(new Question { Topic = "History", Text = "Who was the first president?", Answer = "Washington" });
        session.QuestionsByTour[2] = questions2;

        Console.WriteLine($"Before serialization:");
        Console.WriteLine($"  Tour 1: {session.QuestionsByTour[1].Count} questions");
        Console.WriteLine($"  Tour 2: {session.QuestionsByTour[2].Count} questions");

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters =
            {
                new System.Text.Json.Serialization.JsonStringEnumConverter()
            }
        };

        var json = JsonSerializer.Serialize(session, options);
        Console.WriteLine($"\nSerialized JSON:");
        Console.WriteLine(json);

        var deserialized = JsonSerializer.Deserialize<GameSession>(json, options);
        Console.WriteLine($"\nAfter deserialization:");
        Console.WriteLine($"  Tour 1: {deserialized?.QuestionsByTour[1]?.Count ?? 0} questions");
        Console.WriteLine($"  Tour 2: {deserialized?.QuestionsByTour[2]?.Count ?? 0} questions");

        if (deserialized?.QuestionsByTour[1]?.Count == 2 && deserialized?.QuestionsByTour[2]?.Count == 1)
        {
            Console.WriteLine("\nSUCCESS: Serialization works correctly!");
        }
        else
        {
            Console.WriteLine("\nERROR: Questions were lost during serialization!");
        }
    }
}
