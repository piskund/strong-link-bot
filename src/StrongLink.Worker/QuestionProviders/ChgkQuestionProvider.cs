using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.QuestionProviders;

public sealed class ChgkQuestionProvider : IQuestionProvider
{
    private readonly HttpClient _httpClient;
    private readonly ChgkOptions _options;
    private readonly ILogger<ChgkQuestionProvider> _logger;

    public ChgkQuestionProvider(
        HttpClient httpClient,
        IOptions<ChgkOptions> options,
        ILogger<ChgkQuestionProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public QuestionSourceMode Mode => QuestionSourceMode.Chgk;

    public async Task<IReadOnlyDictionary<int, List<Question>>> PrepareQuestionPoolAsync(
        IReadOnlyList<string> topics,
        int tours,
        int roundsPerTour,
        IReadOnlyList<Player> players,
        GameLanguage language,
        CancellationToken cancellationToken)
    {
        if (language != GameLanguage.Russian)
        {
            throw new InvalidOperationException("ChGK question provider supports Russian language only.");
        }

        var result = new Dictionary<int, List<Question>>();
        var questionsNeeded = Math.Max(1, players.Count) * roundsPerTour * tours;
        var batches = (int)Math.Ceiling((double)questionsNeeded / _options.BatchSize);
        var aggregated = new List<Question>(questionsNeeded + 10);

        for (var i = 0; i < batches; i++)
        {
            var batch = await FetchBatchAsync(_options.BatchSize, cancellationToken);
            aggregated.AddRange(batch);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        var shuffled = aggregated.OrderBy(_ => Guid.NewGuid()).ToList();
        var index = 0;

        for (var tourIndex = 0; tourIndex < tours; tourIndex++)
        {
            var topic = topics.ElementAtOrDefault(tourIndex) ?? $"Тур {tourIndex + 1}";
            var count = Math.Max(1, players.Count) * roundsPerTour;
            var slice = shuffled.Skip(index).Take(count).Select(q => q with { Topic = topic }).ToList();
            index += slice.Count;
            result[tourIndex + 1] = slice;
        }

        return result;
    }

    private async Task<IEnumerable<Question>> FetchBatchAsync(int limit, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(_options.RandomEndpoint)
        {
            Query = $"amount={limit}"
        }.ToString();

        var stream = await _httpClient.GetStreamAsync(uri, cancellationToken);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var root = doc.Root;
        if (root is null)
        {
            return Enumerable.Empty<Question>();
        }

        var entries = root.Elements("question");
        return entries.Select(ParseQuestion).Where(static q => q is not null).Cast<Question>();
    }

    private static Question? ParseQuestion(XElement element)
    {
        var text = element.Element("text")?.Value?.Trim();
        var answer = element.Element("answer")?.Value?.Trim();
        var id = element.Element("id")?.Value;

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
        answer = System.Text.RegularExpressions.Regex.Replace(answer, "<.*?>", string.Empty);

        return new Question
        {
            Topic = string.Empty,
            Text = text,
            Answer = answer,
            SourceId = id,
            SourceName = "ChGK"
        };
    }
}

