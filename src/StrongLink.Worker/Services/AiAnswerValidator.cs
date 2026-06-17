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

            var result = response.Choices.FirstOrDefault()?.Message.Content;
            var isCorrect = InterpretValidationResult(result);

            _logger.LogInformation("AI answer validation result: '{Result}' -> {IsCorrect}", result, isCorrect);
            return isCorrect;
        }
        catch (Exception ex)
        {
            // The AI call can fail for reasons that have nothing to do with the answer — most
            // commonly a 429 when the game is also generating questions against the same OpenAI
            // quota. The old fallback here was an EXACT string comparison, which silently rejected
            // every partial or variant answer ("Грибоедов" vs "Александр Грибоедов", "Фродо" vs
            // "Фродо Бэггинс") whenever the model was unreachable — making the bot feel far stricter
            // than its lenient prompt intends. We instead fall back to a leniency-biased heuristic.
            _logger.LogError(ex, "Failed to validate answer using AI. Falling back to lenient heuristic comparison.");
            return LenientHeuristicMatch(normalizedUser, normalizedCorrect);
        }
    }

    /// <summary>
    /// Offline approximation of the lenient AI judge, used only when the model is unreachable.
    /// Accepts when the answers are equal, when one is a subset of the other's words (covers partial
    /// names like "Грибоедов" ⊂ "Александр Грибоедов" and "Фродо" ⊂ "Фродо Бэггинс"), or when any
    /// significant word matches exactly. Deliberately biased toward accepting — a model outage should
    /// never make grading harsher than the intended mild check.
    /// </summary>
    internal static bool LenientHeuristicMatch(string normalizedUser, string normalizedCorrect)
    {
        if (string.IsNullOrWhiteSpace(normalizedUser) || string.IsNullOrWhiteSpace(normalizedCorrect))
        {
            return string.Equals(normalizedUser, normalizedCorrect, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(normalizedUser, normalizedCorrect, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var userWords = normalizedUser.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var correctWords = normalizedCorrect.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Subset match: every word of the shorter answer appears in the longer one. This is what
        // makes a surname-only or first-name-only answer count for a full name.
        var (shorter, longer) = userWords.Length <= correctWords.Length
            ? (userWords, correctWords)
            : (correctWords, userWords);

        var longerSet = new HashSet<string>(longer, StringComparer.OrdinalIgnoreCase);

        // Subset match, but only when the shorter answer carries at least one significant (3+ char)
        // word — otherwise a lone particle like "де" would match "Шарль де Голль".
        if (shorter.All(w => longerSet.Contains(w)) && shorter.Any(w => w.Length >= 3))
        {
            return true;
        }

        // Otherwise accept if a significant (3+ char) word matches exactly on both sides.
        return shorter.Any(w => w.Length >= 3 && longerSet.Contains(w));
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
        // Baseline rule applied at EVERY difficulty: cosmetic differences are never wrong.
        // The grader was reported to reject answers over typos and dashes even on lenient settings,
        // so we make this non-negotiable and prepend it to the level-specific guidance below.
        var baseline = language == GameLanguage.Russian
            ? "ВАЖНО (действует на ЛЮБОМ уровне сложности): НИКОГДА не отклоняйте ответ только из-за:\n" +
              "   • опечаток, описок или орфографических ошибок\n" +
              "   • дефисов, тире, пробелов и знаков препинания (например, «Кока-Кола» = «Кока Кола»)\n" +
              "   • регистра букв, ё/е, разного порядка слов\n" +
              "Эти различия — НЕ ошибка. Оценивайте только по смыслу.\n\n"
            : "IMPORTANT (applies at EVERY difficulty level): NEVER reject an answer just because of:\n" +
              "   • typos, misspellings, or spelling mistakes\n" +
              "   • hyphens, dashes, spacing, or punctuation (e.g. \"Coca-Cola\" = \"Coca Cola\")\n" +
              "   • letter casing or different word order\n" +
              "These differences are NOT mistakes. Judge by meaning only.\n\n";

        return baseline + difficulty switch
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
        // Use dedicated answer validation model if specified, otherwise use main model
        var modelToUse = _options.AnswerValidationModel ?? _options.Model;

        var body = new OpenAiRequest
        {
            Model = modelToUse,
            Messages =
            [
                new OpenAiMessage("system", "You are a friendly quiz answer judge. Decide whether the user's answer conveys the SAME MEANING as the correct answer. This is a casual party game, so judge generously by sense: accept typos, transliteration and spelling variants, alternative or partial names, synonyms, missing articles/suffixes, and different word order. Only reject answers that name a clearly different thing or are plainly wrong. When in doubt, accept."),
                new OpenAiMessage("user", prompt)
            ],
            Temperature = 0.0 // Use deterministic output for validation
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        _logger.LogDebug("Sending answer validation request to OpenAI using model: {Model}", modelToUse);

        // Retry on 429 (and transient 5xx). Validation shares an OpenAI quota with question
        // generation, so during an active game we routinely hit rate limits; without retries the
        // call fails and validation falls back to a coarse heuristic. A few short backoffs let the
        // real lenient AI judge run instead.
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            // HttpRequestMessage can't be reused across sends, so rebuild it each attempt.
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var payload = await JsonSerializer.DeserializeAsync<OpenAiResponse>(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                return payload ?? throw new InvalidOperationException("OpenAI response payload was null");
            }

            var status = (int)response.StatusCode;
            var retryable = status == 429 || status >= 500;

            if (!retryable || attempt == maxAttempts)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OpenAI API returned {StatusCode} after {Attempts} attempt(s): {Error}",
                    response.StatusCode, attempt, errorContent);
                response.EnsureSuccessStatusCode();
            }

            // Honor a server-provided Retry-After when present, otherwise exponential backoff.
            var delay = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
            _logger.LogWarning("OpenAI validation request got {StatusCode}; retrying in {Delay}ms (attempt {Attempt}/{Max})",
                response.StatusCode, (int)delay.TotalMilliseconds, attempt, maxAttempts);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Treat punctuation, dashes (incl. en/em dashes), and any non-letter/digit symbol as a
        // space, then collapse runs of whitespace to a single space. This means trivial cosmetic
        // differences — "Coca-Cola" vs "Coca Cola", "T. Rex" vs "T Rex", stray punctuation, double
        // spaces — match exactly and never reach the model, so they can never be marked wrong.
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var tokens = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens).Trim();
    }

    /// <summary>
    /// Interprets the model's free-form verdict robustly. The model is asked to reply with a single
    /// word, but in practice it often adds punctuation, markdown, or a short phrase
    /// (e.g. "Верно.", "Да, верно", "**Correct**", "Yes, that's right"). Requiring an exact match
    /// against "correct"/"верно" silently rejects those, making validation far stricter than intended.
    /// We therefore look for any positive signal and only reject on an explicit negative.
    /// </summary>
    internal static bool InterpretValidationResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Empty/garbled response: don't punish the player for a model hiccup.
            return true;
        }

        // Strip markdown emphasis and punctuation, collapse to lowercase for matching.
        var text = raw.ToLowerInvariant();
        var cleaned = new string(text.Select(c => char.IsLetter(c) ? c : ' ').ToArray());
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var positives = new[] { "correct", "yes", "true", "right", "верно", "верный", "верен", "да", "правильно", "правильный", "засчитано" };
        var negatives = new[] { "incorrect", "no", "not", "false", "wrong", "неверно", "неверный", "неверен", "нет", "неправильно", "неправильный", "незасчитано", "незачёт", "незачет" };
        var negators = new[] { "не", "не", "no", "not" };

        var hasNegative = tokens.Any(t => negatives.Contains(t));

        // A positive word negated by a preceding negator ("не верно", "not correct") is actually a rejection.
        var hasPositive = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!positives.Contains(tokens[i]))
            {
                continue;
            }

            var negatedByPrevious = i > 0 && negators.Contains(tokens[i - 1]);
            if (negatedByPrevious)
            {
                hasNegative = true;
            }
            else
            {
                hasPositive = true;
            }
        }

        // Explicit negatives win when both signals are present.
        if (hasNegative)
        {
            return false;
        }

        if (hasPositive)
        {
            return true;
        }

        // No clear verdict word found: fall back to a leniency-biased substring check.
        return text.Contains("верн") || text.Contains("correct") || text.Contains("правильн") || text.Contains("yes");
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
