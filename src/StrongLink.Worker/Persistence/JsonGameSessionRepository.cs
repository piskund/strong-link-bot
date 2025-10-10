using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Persistence;

public sealed class JsonGameSessionRepository : IGameSessionRepository
{
    private readonly ILogger<JsonGameSessionRepository> _logger;
    private readonly BotOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public JsonGameSessionRepository(
        ILogger<JsonGameSessionRepository> logger,
        IOptions<BotOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters =
            {
                new System.Text.Json.Serialization.JsonStringEnumConverter()
            }
        };

        Directory.CreateDirectory(_options.StateStoragePath);
    }

    public async Task SaveAsync(GameSession session, CancellationToken cancellationToken)
    {
        var path = ResolvePath(session.ChatId);
        var json = JsonSerializer.Serialize(session, _serializerOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        _logger.LogInformation("Game session {Id} saved to {Path}", session.Id, path);
    }

    public async Task<GameSession?> LoadAsync(long chatId, CancellationToken cancellationToken)
    {
        var path = ResolvePath(chatId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var session = await JsonSerializer.DeserializeAsync<GameSession>(stream, _serializerOptions, cancellationToken);
        return session;
    }

    public Task RemoveAsync(long chatId, CancellationToken cancellationToken)
    {
        var path = ResolvePath(chatId);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Removed game session state for chat {ChatId}", chatId);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(long chatId) => Path.Combine(_options.StateStoragePath, $"session_{chatId}.json");
}

