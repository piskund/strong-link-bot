using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.QuestionProviders;

namespace StrongLink.Worker.Tests.QuestionProviders;

/// <summary>
/// Verifies the Scriban-rendered generation prompt carries the right guidance for each branch
/// (language, difficulty, topic kind, mature content, archived exclusions). We assert on the
/// spirit/intent fragments rather than exact byte output.
/// </summary>
public class BuildPromptTemplateTests
{
    private static AiQuestionProvider CreateProvider()
    {
        var handler = new NoopHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        return new AiQuestionProvider(
            client,
            Options.Create(new OpenAiOptions { ApiKey = "test", Model = "gpt-test", ImagePercentage = 30 }),
            new LocalizationService(),
            NullLogger<AiQuestionProvider>.Instance);
    }

    [Fact]
    public void English_FixedTopic_Easy_FamilyFriendly()
    {
        var prompt = CreateProvider().BuildPrompt(
            GameLanguage.English, "History", 8, matureContent: false, DifficultyLevel.Easy);

        Assert.Contains("topic \"History\"", prompt);
        Assert.Contains("Generate 8 questions", prompt);
        Assert.Contains("EASY LEVEL", prompt);
        Assert.Contains("Family-Friendly Content", prompt);
        Assert.DoesNotContain("Mature Content", prompt);
        // 30% of 8 = ceil(2.4) = 3 images
        Assert.Contains("approximately 3 questions", prompt);
        Assert.Contains("Question: <question text>?", prompt);
        // No archived-exclusion block when none supplied.
        Assert.DoesNotContain("STRICTLY AVOID DUPLICATES", prompt);
        // Movie guidance only for movie topics.
        Assert.DoesNotContain("MOVIES/CINEMA", prompt);
    }

    [Fact]
    public void English_RandomTopic_Mature_Hard()
    {
        var prompt = CreateProvider().BuildPrompt(
            GameLanguage.English, "random", 5, matureContent: true, DifficultyLevel.Hard, isRandomTopic: true);

        Assert.Contains("FIRST choose a random interesting trivia topic", prompt);
        Assert.Contains("Generate 5 questions about your chosen topic", prompt);
        Assert.Contains("HARD LEVEL", prompt);
        Assert.Contains("Mature Content (18+)", prompt);
        // The literal "random" must not leak in as a topic name.
        Assert.DoesNotContain("topic \"random\"", prompt);
    }

    [Fact]
    public void English_MovieTopic_IncludesMovieGuidance()
    {
        var prompt = CreateProvider().BuildPrompt(
            GameLanguage.English, "Movies", 6, matureContent: false, DifficultyLevel.Medium);

        Assert.Contains("MOVIES/CINEMA", prompt);
        Assert.Contains("AVOID TECHNICAL ASPECTS", prompt);
        Assert.Contains("MEDIUM LEVEL", prompt);
    }

    [Fact]
    public void English_WithArchived_IncludesExclusionBlock()
    {
        var archived = new List<Question>
        {
            new() { Topic = "History", Text = "Who was the first Roman emperor?", Answer = "Augustus", SourceName = "OpenAI" },
            new() { Topic = "History", Text = "In what year did WWII end?", Answer = "1945", SourceName = "OpenAI" }
        };

        var prompt = CreateProvider().BuildPrompt(
            GameLanguage.English, "History", 4, matureContent: false, DifficultyLevel.Easy, archived);

        Assert.Contains("STRICTLY AVOID DUPLICATES", prompt);
        Assert.Contains("Who was the first Roman emperor? → Augustus", prompt);
        Assert.Contains("In what year did WWII end? → 1945", prompt);
    }

    [Fact]
    public void Russian_FixedTopic_Medium_UsesRussianCopy()
    {
        var prompt = CreateProvider().BuildPrompt(
            GameLanguage.Russian, "Наука", 3, matureContent: false, DifficultyLevel.Medium);

        Assert.Contains("по теме \"Наука\"", prompt);
        Assert.Contains("Сгенерируйте 3 вопросов", prompt);
        Assert.Contains("СРЕДНИЙ УРОВЕНЬ", prompt);
        Assert.Contains("Семейный контент", prompt);
        Assert.Contains("Вопрос: <текст вопроса>?", prompt);
    }

    [Fact]
    public void Russian_MovieTopic_IncludesRussianMovieGuidance()
    {
        var prompt = CreateProvider().BuildPrompt(
            GameLanguage.Russian, "фильмы", 5, matureContent: false, DifficultyLevel.Hard);

        Assert.Contains("КИНО/ФИЛЬМЫ", prompt);
        Assert.Contains("СЛОЖНЫЙ УРОВЕНЬ", prompt);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
