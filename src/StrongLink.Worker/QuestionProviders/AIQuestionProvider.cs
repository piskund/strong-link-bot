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
    private readonly LocalizationService _localizationService;
    private readonly ILogger<AiQuestionProvider> _logger;

    public AiQuestionProvider(
        HttpClient httpClient,
        IOptions<OpenAiOptions> options,
        LocalizationService localizationService,
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
        var result = new Dictionary<int, List<Question>>();

        for (var tourIndex = 0; tourIndex < tours; tourIndex++)
        {
            var topic = topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
            var questionsNeeded = Math.Max(1, players.Count) * roundsPerTour;
            _logger.LogInformation("Requesting {Count} AI questions for topic {Topic}", questionsNeeded, topic);

            var prompt = BuildPrompt(language, topic, questionsNeeded);
            var response = await RequestOpenAiAsync(prompt, cancellationToken);
            var parsed = ParseQuestions(response, topic);

            result[tourIndex + 1] = parsed.Take(questionsNeeded).ToList();
        }

        return result;
    }

    private string BuildPrompt(GameLanguage language, string topic, int questions)
    {
        var pack = _localizationService.GetLanguagePack(language);
        var instruction = language == GameLanguage.Russian
            ?
            "Сгенерируй {0} викторинных вопросов на русском языке по теме \"{1}\". Формат каждого блока:\nВопрос: <текст вопроса>?\nОтвет: <короткий точный ответ>."
            :
            "Generate {0} trivia questions in English on the topic \"{1}\". Format each block as:\nQuestion: <question text>?\nAnswer: <short precise answer>.";

        return string.Format(instruction, questions, topic);
    }

    private async Task<OpenAiResponse> RequestOpenAiAsync(string prompt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var body = new OpenAiRequest
        {
            Model = _options.Model,
            Messages =
            [
                new OpenAiMessage("system", "You are a helpful trivia generation assistant."),
                new OpenAiMessage("user", prompt)
            ]
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await JsonSerializer.DeserializeAsync<OpenAiResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return payload ?? throw new InvalidOperationException("OpenAI response payload was null");
    }

    private static IEnumerable<Question> ParseQuestions(OpenAiResponse response, string topic)
    {
        var content = response.Choices.Select(c => c.Message.Content).FirstOrDefault() ?? string.Empty;
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? questionText = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("Question:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Вопрос:", StringComparison.OrdinalIgnoreCase))
            {
                questionText = line[(line.IndexOf(':') + 1)..].Trim().TrimEnd('?');
            }
            else if (line.StartsWith("Answer:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Ответ:", StringComparison.OrdinalIgnoreCase))
            {
                var answer = line[(line.IndexOf(':') + 1)..].Trim().TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(questionText) && !string.IsNullOrWhiteSpace(answer))
                {
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

