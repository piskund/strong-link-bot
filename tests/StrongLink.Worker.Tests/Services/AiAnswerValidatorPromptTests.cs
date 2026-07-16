using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Tests.Services;

/// <summary>
/// Verifies the Scriban-rendered answer-validation prompt carries the right strictness guidance per
/// language and difficulty, and always includes the baseline "cosmetic differences are never wrong"
/// rule and the one-word verdict instruction.
/// </summary>
public class AiAnswerValidatorPromptTests
{
    private static string Build(string user, string correct, string question, GameLanguage language, DifficultyLevel difficulty)
    {
        var validator = new AiAnswerValidator(
            new HttpClient(new NoopHandler()) { BaseAddress = new Uri("https://api.openai.com/") },
            Options.Create(new OpenAiOptions { ApiKey = "test", Model = "gpt-test" }),
            NullLogger<AiAnswerValidator>.Instance);

        var method = typeof(AiAnswerValidator).GetMethod(
            "BuildValidationPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (string)method.Invoke(null, new object[] { user, correct, question, language, difficulty })!;
    }

    [Fact]
    public void English_Easy_IsLenientAndHasBaselineAndVerdict()
    {
        var prompt = Build("Augustus", "Octavian Augustus", "First Roman emperor?", GameLanguage.English, DifficultyLevel.Easy);

        Assert.Contains("Question: First Roman emperor?", prompt);
        Assert.Contains("Correct answer: Octavian Augustus", prompt);
        Assert.Contains("User's answer: Augustus", prompt);
        Assert.Contains("applies at EVERY difficulty level", prompt);
        Assert.Contains("EASY LEVEL - BE LENIENT", prompt);
        Assert.Contains("Answer with just one word: 'Correct' or 'Incorrect'.", prompt);
        Assert.DoesNotContain("STRICT VALIDATION", prompt);
    }

    [Fact]
    public void English_Hard_IsStrict()
    {
        var prompt = Build("a", "b", "q?", GameLanguage.English, DifficultyLevel.Hard);

        Assert.Contains("HARD LEVEL - STRICT VALIDATION", prompt);
        Assert.DoesNotContain("BE LENIENT", prompt);
    }

    [Fact]
    public void Russian_Medium_UsesRussianCopy()
    {
        var prompt = Build("Москва", "Москва", "Столица России?", GameLanguage.Russian, DifficultyLevel.Medium);

        Assert.Contains("Вопрос: Столица России?", prompt);
        Assert.Contains("действует на ЛЮБОМ уровне сложности", prompt);
        Assert.Contains("СРЕДНИЙ УРОВЕНЬ", prompt);
        Assert.Contains("Ответьте только одним словом: 'Верно' или 'Неверно'.", prompt);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
