using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class JoinCommandHandler : CommandHandlerBase
{
    private readonly ILogger<JoinCommandHandler> _logger;
    private readonly BotOptions _botOptions;
    private readonly GameOptions _gameOptions;

    public JoinCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IOptions<BotOptions> botOptions,
        IOptions<GameOptions> gameOptions,
        ILogger<JoinCommandHandler> logger)
        : base(client, localization, repository)
    {
        _logger = logger;
        _botOptions = botOptions.Value;
        _gameOptions = gameOptions.Value;
    }

    public override string Command => "/join";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /join command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}, creating new session", chatId);

            // Auto-create session for better UX
            session = new GameSession
            {
                ChatId = chatId,
                Language = _botOptions.DefaultLanguage,
                QuestionSourceMode = _botOptions.QuestionSource,
                Topics = _gameOptions.Topics,
                Tours = _gameOptions.Tours,
                RoundsPerTour = _gameOptions.RoundsPerTour,
                AnswerTimeoutSeconds = _gameOptions.AnswerTimeoutSeconds,
                EliminateLowest = _gameOptions.EliminateLowest,
                Status = GameStatus.AwaitingPlayers
            };

            _logger.LogInformation("Created new session for chat {ChatId}", chatId);
        }

        var from = message.From;
        if (from is null)
        {
            _logger.LogWarning("Message from null user in chat {ChatId}", chatId);
            return;
        }

        var language = session.Language;
        var existing = session.FindPlayer(from.Id);
        if (existing is not null)
        {
            _logger.LogDebug("User {UserId} already joined as {PlayerName}", from.Id, existing.DisplayName);
            var text = string.Format(Localization.GetString(language, "Bot.AlreadyJoined"), existing.DisplayName);
            await Client.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
            return;
        }

        var player = new Player
        {
            Id = from.Id,
            DisplayName = from.Username is { Length: > 0 } ? $"@{from.Username}" : from.FirstName ?? "Player",
            Status = PlayerStatus.Active
        };

        _logger.LogInformation("Adding player {PlayerName} (ID: {PlayerId}) to session. Current player count: {CurrentCount}",
            player.DisplayName, player.Id, session.Players.Count);

        session.Players.Add(player);

        _logger.LogInformation("Player added. New player count: {PlayerCount}. Saving session...",
            session.Players.Count);

        await Repository.SaveAsync(session, cancellationToken);

        // Verify the save worked
        var verifySession = await Repository.LoadAsync(chatId, cancellationToken);
        _logger.LogInformation("Verification after save: Session loaded with {PlayerCount} players",
            verifySession?.Players.Count ?? 0);

        _logger.LogInformation("Player {PlayerName} (ID: {PlayerId}) joined game in chat {ChatId}. Total players: {PlayerCount}",
            player.DisplayName, player.Id, chatId, session.Players.Count);

        var joinedText = string.Format(Localization.GetString(language, "Bot.Joined"), player.DisplayName);
        await Client.SendTextMessageAsync(chatId, joinedText, cancellationToken: cancellationToken);
    }
}

