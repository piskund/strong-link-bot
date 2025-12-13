using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.QuestionProviders;

public sealed class AiQuestionProvider : IQuestionProvider
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<AiQuestionProvider> _logger;

    public AiQuestionProvider(
        HttpClient httpClient,
        IOptions<OpenAiOptions> options,
        ILocalizationService localizationService,
        ILogger<AiQuestionProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _localizationService = localizationService;
        _logger = logger;
    }

    public QuestionSourceMode Mode => QuestionSourceMode.AI;

    public async Task<IReadOnlyDictionary<int, List<Question>>> PrepareQuestionPoolAsync(
        IReadOnlyList<string> topics,
        int tours,
        int roundsPerTour,
        IReadOnlyList<Player> players,
        GameLanguage language,
        CancellationToken cancellationToken)
    {
        return await PrepareQuestionPoolAsync(topics, tours, roundsPerTour, players, language, null, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, List<Question>>> PrepareQuestionPoolAsync(
        IReadOnlyList<string> topics,
        int tours,
        int roundsPerTour,
        IReadOnlyList<Player> players,
        GameLanguage language,
        IReadOnlyList<Question>? archivedQuestions,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, List<Question>>();

        for (var tourIndex = 0; tourIndex < tours; tourIndex++)
        {
            try
            {
                var topic = topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
                var questionsNeeded = Math.Max(1, players.Count) * roundsPerTour;
                _logger.LogInformation("Requesting {Count} AI questions for topic {Topic}", questionsNeeded, topic);

                var prompt = BuildPrompt(language, topic, questionsNeeded, archivedQuestions);
                _logger.LogDebug("Prompt: {Prompt}", prompt);

                var response = await RequestOpenAiAsync(prompt, cancellationToken);
                _logger.LogDebug("Received response from OpenAI");

                var parsed = ParseQuestions(response, topic);
                var questionList = parsed.ToList();  // Keep ALL parsed questions, not just the needed amount
                _logger.LogInformation("Parsed {Count} questions from OpenAI response (requested {Needed})",
                    questionList.Count, questionsNeeded);

                result[tourIndex + 1] = questionList;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate questions for tour {Tour}", tourIndex + 1);
                result[tourIndex + 1] = new List<Question>();
            }
        }

        return result;
    }

    private string BuildPrompt(GameLanguage language, string topic, int questions, IReadOnlyList<Question>? archivedQuestions = null)
    {
        var pack = _localizationService.GetLanguagePack(language);

        // Build context from archived questions to avoid repetition
        var archivedContext = "";
        if (archivedQuestions != null && archivedQuestions.Count > 0)
        {
            // Take up to 30 most recent archived questions for context
            var recentArchived = archivedQuestions
                .TakeLast(30)
                .Select(q => $"- {q.Text}")
                .ToList();

            if (language == GameLanguage.Russian)
            {
                archivedContext = "\n\nВАЖНО: Не повторяйте эти вопросы, которые уже были заданы ранее:\n" +
                    string.Join("\n", recentArchived) + "\n\n" +
                    "Создайте НОВЫЕ вопросы, которые отличаются от приведённых выше.\n";
            }
            else
            {
                archivedContext = "\n\nIMPORTANT: Do NOT repeat these questions that have been asked before:\n" +
                    string.Join("\n", recentArchived) + "\n\n" +
                    "Create NEW questions that are different from the ones listed above.\n";
            }
        }

        var instruction = language == GameLanguage.Russian
            ?
            "Составьте набор викторинных вопросов на русском языке по теме \"{1}\". Учитывайте следующие требования:\n" +
            "- Сгенерируйте {0} вопросов по теме \"{1}\".\n" +
            "- Каждый вопрос должен быть понятным и коротким, рассчитан на широкую аудиторию (не только знатоков).\n" +
            "- К каждому вопросу дайте правильный краткий ответ (одно слово или несколько слов).\n" +
            "- Вопросы должны быть интересными и заставлять думать, но не слишком сложными.\n" +
            "- Избегайте двусмысленных вопросов - у каждого вопроса должен быть один чёткий правильный ответ.\n" +
            "- Вопросы должны покрывать разные аспекты темы.\n" +
            archivedContext +
            "- Оформите вывод в точном формате:\n" +
            "Вопрос: <текст вопроса>?\n" +
            "Ответ: <текст ответа>\n" +
            "(Не добавляйте никаких пояснений или комментариев.)"
            :
            "Create a set of trivia questions in English on the topic \"{1}\". Follow these requirements:\n" +
            "- Generate {0} questions about the topic \"{1}\".\n" +
            "- Each question should be clear and concise, suitable for a general audience (non-experts).\n" +
            "- Provide a correct short answer for each question (one word or a few words at most).\n" +
            "- Questions should be interesting and thought-provoking, but not too difficult.\n" +
            "- Avoid ambiguous questions - each should have a single clear correct answer.\n" +
            "- Ensure the questions are varied and cover different aspects of the topic.\n" +
            archivedContext +
            "- Format the output exactly as:\n" +
            "Question: <question text>?\n" +
            "Answer: <answer text>\n" +
            "(No additional explanations or commentary.)";

        return string.Format(instruction, questions, topic);
    }

    private async Task<OpenAiResponse> RequestOpenAiAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            var body = new OpenAiRequest
            {
                Model = _options.Model,
                Messages =
                [
                    new OpenAiMessage("system", "You are a trivia question generator. Create clear, engaging trivia questions suitable for a quiz game. Each question should have a single unambiguous correct answer."),
                    new OpenAiMessage("user", prompt)
                ]
            };

            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Sending request to OpenAI: {Endpoint}", _options.Endpoint);
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
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request to OpenAI failed");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request questions from OpenAI");
            throw;
        }
    }

    private IEnumerable<Question> ParseQuestions(OpenAiResponse response, string topic)
    {
        var content = response.Choices.Select(c => c.Message.Content).FirstOrDefault() ?? string.Empty;

        _logger.LogInformation("OpenAI response content:\n{Content}", content);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _logger.LogInformation("Split into {Count} lines", lines.Length);

        string? questionText = null;
        var parsedCount = 0;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            _logger.LogDebug("Processing line: {Line}", trimmedLine);

            if (trimmedLine.StartsWith("Question:", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("Вопрос:", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("Q:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmedLine.IndexOf(':');
                questionText = trimmedLine[(colonIndex + 1)..].Trim().TrimEnd('?');
                _logger.LogDebug("Found question: {Question}", questionText);
            }
            else if (trimmedLine.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase) ||
                     trimmedLine.StartsWith("Ответ:", StringComparison.OrdinalIgnoreCase) ||
                     trimmedLine.StartsWith("A:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmedLine.IndexOf(':');
                var answer = trimmedLine[(colonIndex + 1)..].Trim().TrimEnd('.');
                _logger.LogDebug("Found answer: {Answer}", answer);

                if (!string.IsNullOrWhiteSpace(questionText) && !string.IsNullOrWhiteSpace(answer))
                {
                    parsedCount++;
                    _logger.LogDebug("Successfully parsed question #{Count}", parsedCount);
                    yield return new Question
                    {
                        Topic = topic,
                        Text = questionText + '?',
                        Answer = answer,
                        SourceName = "OpenAI"
                    };
                }

                questionText = null;
            }
        }

        _logger.LogInformation("Total questions parsed: {Count}", parsedCount);
    }

    private sealed record OpenAiRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<OpenAiMessage> Messages { get; init; }
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

