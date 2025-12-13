using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.QuestionProviders;

public sealed class JsonQuestionProvider : IQuestionProvider
{
	private readonly ILogger<JsonQuestionProvider> _logger;

	public JsonQuestionProvider(ILogger<JsonQuestionProvider> logger)
	{
		_logger = logger;
	}

	public QuestionSourceMode Mode => QuestionSourceMode.Json;

	public Task<IReadOnlyDictionary<int, List<Question>>> PrepareQuestionPoolAsync(
		IReadOnlyList<string> topics,
		int tours,
		int roundsPerTour,
		IReadOnlyList<Player> players,
		GameLanguage language,
		CancellationToken cancellationToken)
	{
		return Task.FromException<IReadOnlyDictionary<int, List<Question>>>(
			new InvalidOperationException("Use PrepareFromFileAsync on JsonQuestionProvider via Standalone runner."));
	}

	public async Task<IReadOnlyDictionary<int, List<Question>>> PrepareFromFileAsync(
		string filePath,
		IReadOnlyList<string> topics,
		int tours,
		int roundsPerTour,
		IReadOnlyList<Player> players,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(filePath))
		{
			throw new FileNotFoundException("Question pool JSON file not found", filePath);
		}

		await using var stream = File.OpenRead(filePath);
		var raw = await JsonSerializer.DeserializeAsync<List<JsonQuestion>>(stream, cancellationToken: cancellationToken)
			?? new List<JsonQuestion>();

		// basic validation and normalization
		var normalized = raw
			.Where(q => !string.IsNullOrWhiteSpace(q.Text) && !string.IsNullOrWhiteSpace(q.Answer))
			.Select(q => new Question
			{
				Topic = q.Topic?.Trim() ?? string.Empty,
				Text = q.Text.Trim(),
				Answer = q.Answer.Trim(),
				SourceName = "JSON"
			})
			.ToList();

		// deduplicate by normalized text
		var dedup = new List<Question>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var q in normalized)
		{
			var key = Normalize(q.Text);
			if (seen.Add(key))
			{
				dedup.Add(q);
			}
		}

		var result = new Dictionary<int, List<Question>>();
		var perTopic = dedup
			.GroupBy(q => string.IsNullOrWhiteSpace(q.Topic) ? "#generic" : q.Topic)
			.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

		var requiredPerTour = Math.Max(1, players.Count) * roundsPerTour;

		for (var tourIndex = 0; tourIndex < tours; tourIndex++)
		{
			var topic = topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
			var bucket = new List<Question>(requiredPerTour + 4);

			if (perTopic.TryGetValue(topic, out var specific))
			{
				bucket.AddRange(specific);
			}
			if (bucket.Count < requiredPerTour && perTopic.TryGetValue("#generic", out var generic))
			{
				bucket.AddRange(generic);
			}

			var selected = bucket.Take(requiredPerTour).Select(q => q with { Topic = topic }).ToList();
			result[tourIndex + 1] = selected;
		}

		var total = result.Sum(p => p.Value.Count);
		_logger.LogInformation("Loaded {Count} questions from JSON file {File}", total, filePath);
		return result;
	}

	private static string Normalize(string value)
	{
		return value.Trim().Trim('.', '!', '?', '\'', '"');
	}

	private sealed record JsonQuestion
	{
		[JsonPropertyName("topic")]
		public string? Topic { get; init; }

		[JsonPropertyName("text")]
		public required string Text { get; init; }

		[JsonPropertyName("answer")]
		public required string Answer { get; init; }
	}
}


