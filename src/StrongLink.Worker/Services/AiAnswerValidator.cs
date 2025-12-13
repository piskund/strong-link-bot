using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

public sealed class AiAnswerValidator : IAnswerValidator
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<AiAnswerValidator> _logger;

    public AiAnswerValidator(
        HttpClient httpClient,
        IOptions<OpenAiOptions> options,
        ILogger<AiAnswerValidator> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> ValidateAnswerAsync(
        string userAnswer,
        string correctAnswer,
        string question,
        GameLanguage language,
        CancellationToken cancellationToken)
    {
        // First try simple normalization for exact matches (to save API calls)
        var normalizedUser = Normalize(userAnswer);
        var normalizedCorrect = Normalize(correctAnswer);

        if (string.Equals(normalizedUser, normalizedCorrect, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Answer matched exactly: '{UserAnswer}' == '{CorrectAnswer}'", userAnswer, correctAnswer);
            return true;
        }

        // If not an exact match, use AI to check semantic equivalence
        try
        {
            _logger.LogDebug("Using AI to validate answer. User: '{UserAnswer}', Correct: '{CorrectAnswer}'",
                userAnswer, correctAnswer);

            var prompt = BuildValidationPrompt(userAnswer, correctAnswer, question, language);
            var response = await RequestOpenAiAsync(prompt, cancellationToken);

            var result = response.Choices.FirstOrDefault()?.Message.Content?.Trim().ToLowerInvariant();
            var isCorrect = result == "correct" || result == "yes" || result == "верно" || result == "да";

            _logger.LogInformation("AI answer validation result: '{Result}' -> {IsCorrect}", result, isCorrect);
            return isCorrect;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate answer using AI. Falling back to string comparison.");
            // Fall back to simple string comparison on error
            return string.Equals(normalizedUser, normalizedCorrect, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string BuildValidationPrompt(string userAnswer, string correctAnswer, string question, GameLanguage language)
    {
        return language == GameLanguage.Russian
            ? $"Вопрос: {question}\n\n" +
              $"Правильный ответ: {correctAnswer}\n" +
              $"Ответ пользователя: {userAnswer}\n\n" +
              $"Является ли ответ пользователя семантически правильным? Учитывайте небольшие орфографические различия, " +
              $"разный порядок слов, сокращения и синонимы. Ответьте только одним словом: 'Верно' или 'Неверно'."
            : $"Question: {question}\n\n" +
              $"Correct answer: {correctAnswer}\n" +
              $"User's answer: {userAnswer}\n\n" +
              $"Is the user's answer semantically correct? Consider minor spelling differences, " +
              $"word order variations, abbreviations, and synonyms. Answer with just one word: 'Correct' or 'Incorrect'.";
    }

    private async Task<OpenAiResponse> RequestOpenAiAsync(string prompt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

        // Use dedicated answer validation model if specified, otherwise use main model
        var modelToUse = _options.AnswerValidationModel ?? _options.Model;

        var body = new OpenAiRequest
        {
            Model = modelToUse,
            Messages =
            [
                new OpenAiMessage("system", "You are an answer validation assistant. Your job is to determine if a user's answer is semantically equivalent to the correct answer, accounting for minor variations."),
                new OpenAiMessage("user", prompt)
            ],
            Temperature = 0.0 // Use deterministic output for validation
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogDebug("Sending answer validation request to OpenAI using model: {Model}", modelToUse);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenAI API returned {StatusCode}: {Error}", response.StatusCode, errorContent);
            response.EnsureSuccessStatusCode();
        }

        var payload = await JsonSerializer.DeserializeAsync<OpenAiResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        return payload ?? throw new InvalidOperationException("OpenAI response payload was null");
    }

    private static string Normalize(string value)
    {
        return value.Trim().Trim('.', '!', '?', '\'', '"');
    }

    private sealed record OpenAiRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<OpenAiMessage> Messages { get; init; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }
    }

    private sealed record OpenAiMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OpenAiResponse
    {
        [JsonPropertyName("choices")]
        public required IReadOnlyList<Choice> Choices { get; init; }

        public sealed record Choice
        {
            [JsonPropertyName("message")]
            public required OpenAiMessage Message { get; init; }
        }
    }
}
