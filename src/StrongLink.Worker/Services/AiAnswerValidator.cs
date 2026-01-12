using System.Text.Json;
using System.Text.Json.Serialization;
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
        DifficultyLevel difficultyLevel,
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

            var prompt = BuildValidationPrompt(userAnswer, correctAnswer, question, language, difficultyLevel);
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

    private string BuildValidationPrompt(string userAnswer, string correctAnswer, string question, GameLanguage language, DifficultyLevel difficultyLevel)
    {
        var strictnessGuidance = GetStrictnessGuidance(difficultyLevel, language);

        return language == GameLanguage.Russian
            ? $"Вопрос: {question}\n\n" +
              $"Правильный ответ: {correctAnswer}\n" +
              $"Ответ пользователя: {userAnswer}\n\n" +
              strictnessGuidance +
              $"Ответьте только одним словом: 'Верно' или 'Неверно'."
            : $"Question: {question}\n\n" +
              $"Correct answer: {correctAnswer}\n" +
              $"User's answer: {userAnswer}\n\n" +
              strictnessGuidance +
              $"Answer with just one word: 'Correct' or 'Incorrect'.";
    }

    private static string GetStrictnessGuidance(DifficultyLevel difficulty, GameLanguage language)
    {
        return difficulty switch
        {
            DifficultyLevel.Easy => language == GameLanguage.Russian
                ? "Является ли ответ пользователя ДОСТАТОЧНО БЛИЗКИМ к правильному?\n\n" +
                  "🟢 ЛЕГКИЙ УРОВЕНЬ - БУДЬТЕ СНИСХОДИТЕЛЬНЫ:\n" +
                  "✅ ПРИНИМАЙТЕ ответы, если:\n" +
                  "   • Основной смысл совпадает (даже если формулировка отличается)\n" +
                  "   • Есть небольшие орфографические ошибки\n" +
                  "   • Использованы синонимы или близкие понятия\n" +
                  "   • Указана только часть ответа, но ключевая\n" +
                  "   • Порядок слов отличается\n\n" +
                  "❌ ОТКЛОНЯЙТЕ только если:\n" +
                  "   • Ответ явно неправильный по смыслу\n" +
                  "   • Указано совершенно другое понятие\n\n"
                : "Is the user's answer CLOSE ENOUGH to the correct answer?\n\n" +
                  "🟢 EASY LEVEL - BE LENIENT:\n" +
                  "✅ ACCEPT answers if:\n" +
                  "   • The core meaning matches (even if wording differs)\n" +
                  "   • There are minor spelling mistakes\n" +
                  "   • Synonyms or related terms are used\n" +
                  "   • Only part of the answer is given, but it's the key part\n" +
                  "   • Word order is different\n\n" +
                  "❌ REJECT only if:\n" +
                  "   • The answer is clearly wrong in meaning\n" +
                  "   • A completely different concept is stated\n\n",

            DifficultyLevel.Medium => language == GameLanguage.Russian
                ? "Является ли ответ пользователя семантически правильным?\n\n" +
                  "🟡 СРЕДНИЙ УРОВЕНЬ - СБАЛАНСИРОВАННАЯ ПРОВЕРКА:\n" +
                  "✅ Учитывайте:\n" +
                  "   • Небольшие орфографические различия\n" +
                  "   • Разный порядок слов\n" +
                  "   • Сокращения и распространённые синонимы\n" +
                  "   • Близкие по смыслу формулировки\n\n" +
                  "❌ Требуйте точность в:\n" +
                  "   • Ключевых терминах и именах\n" +
                  "   • Основном смысле ответа\n\n"
                : "Is the user's answer semantically correct?\n\n" +
                  "🟡 MEDIUM LEVEL - BALANCED VALIDATION:\n" +
                  "✅ Consider:\n" +
                  "   • Minor spelling differences\n" +
                  "   • Word order variations\n" +
                  "   • Abbreviations and common synonyms\n" +
                  "   • Close semantic formulations\n\n" +
                  "❌ Require accuracy in:\n" +
                  "   • Key terms and names\n" +
                  "   • Core meaning of the answer\n\n",

            DifficultyLevel.Hard => language == GameLanguage.Russian
                ? "Является ли ответ пользователя семантически правильным?\n\n" +
                  "🔴 СЛОЖНЫЙ УРОВЕНЬ - СТРОГАЯ ПРОВЕРКА:\n" +
                  "✅ Принимайте только если:\n" +
                  "   • Смысл полностью совпадает\n" +
                  "   • Допустимы только очевидные синонимы\n" +
                  "   • Порядок слов может отличаться, но термины точные\n" +
                  "   • Минимальные орфографические ошибки (1-2 буквы)\n\n" +
                  "❌ Будьте строги к:\n" +
                  "   • Неточным формулировкам\n" +
                  "   • Частичным ответам\n" +
                  "   • Приблизительным определениям\n\n"
                : "Is the user's answer semantically correct?\n\n" +
                  "🔴 HARD LEVEL - STRICT VALIDATION:\n" +
                  "✅ Accept only if:\n" +
                  "   • Meaning matches completely\n" +
                  "   • Only obvious synonyms are acceptable\n" +
                  "   • Word order can differ but terms must be precise\n" +
                  "   • Minimal spelling errors (1-2 letters)\n\n" +
                  "❌ Be strict about:\n" +
                  "   • Imprecise formulations\n" +
                  "   • Partial answers\n" +
                  "   • Approximate definitions\n\n",

            _ => language == GameLanguage.Russian
                ? "Является ли ответ пользователя семантически правильным? Учитывайте небольшие орфографические различия, разный порядок слов, сокращения и синонимы.\n\n"
                : "Is the user's answer semantically correct? Consider minor spelling differences, word order variations, abbreviations, and synonyms.\n\n"
        };
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
