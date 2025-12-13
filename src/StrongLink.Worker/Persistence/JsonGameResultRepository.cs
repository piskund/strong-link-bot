using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Persistence;

public sealed class JsonGameResultRepository : IGameResultRepository
{
    private readonly ILogger<JsonGameResultRepository> _logger;
    private readonly BotOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public JsonGameResultRepository(
        ILogger<JsonGameResultRepository> logger,
        IOptions<BotOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                new System.Text.Json.Serialization.JsonStringEnumConverter()
            }
        };

        Directory.CreateDirectory(_options.ResultsStoragePath);
    }

    public async Task ArchiveAsync(GameResult result, CancellationToken cancellationToken)
    {
        var timestamp = result.CompletedAt.ToString("yyyyMMdd_HHmmss");
        var filename = $"game_{result.ChatId}_{timestamp}_{result.GameId}.json";
        var path = Path.Combine(_options.ResultsStoragePath, filename);

        var json = JsonSerializer.Serialize(result, _serializerOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);

        _logger.LogInformation("Archived game result {GameId} to {Path}. Winner: {Winner}, Duration: {Duration}",
            result.GameId,
            path,
            result.Players.FirstOrDefault(p => p.Placement == 1)?.DisplayName ?? "None",
            result.Duration);
    }
}
