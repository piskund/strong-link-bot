using System.Text;
using Microsoft.Extensions.Logging;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class StandingsCommandHandler : CommandHandlerBase
{
    private readonly ILogger<StandingsCommandHandler> _logger;

    public StandingsCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        ILogger<StandingsCommandHandler> logger)
        : base(client, localization, repository)
    {
        _logger = logger;
    }

    public override string Command => "/standings";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /standings command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}", chatId);
            return;
        }

        var language = session.Language;
        if (session.Players.Count == 0)
        {
            _logger.LogInformation("No players in session for chat {ChatId}", chatId);
            var text = Localization.GetString(language, "Bot.NoPlayers");
            await Client.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Generating standings for chat {ChatId}: {PlayerCount} players, Status: {Status}",
            chatId, session.Players.Count, session.Status);

        var sb = new StringBuilder();
        sb.AppendLine(Localization.GetString(language, "Game.StandingsHeader"));
        sb.AppendLine();

        var ranking = session.Players
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.CorrectAnswers)
            .ThenBy(p => p.IncorrectAnswers)
            .ToList();

        var position = 1;
        foreach (var player in ranking)
        {
            var statusIcon = player.Status switch
            {
                Domain.PlayerStatus.Active => "✅",
                Domain.PlayerStatus.Eliminated => "❌",
                Domain.PlayerStatus.Pending => "⏳",
                _ => "⚪"
            };

            var statusText = player.Status switch
            {
                Domain.PlayerStatus.Active => language == Domain.GameLanguage.Russian ? "в игре" : "active",
                Domain.PlayerStatus.Eliminated => language == Domain.GameLanguage.Russian ? "выбыл" : "eliminated",
                Domain.PlayerStatus.Pending => language == Domain.GameLanguage.Russian ? "ожидает" : "waiting",
                _ => string.Empty
            };

            // Show detailed stats if game has started or completed
            if (session.Status != Domain.GameStatus.NotConfigured &&
                session.Status != Domain.GameStatus.AwaitingPlayers &&
                session.Status != Domain.GameStatus.ReadyToStart)
            {
                sb.AppendLine($"{statusIcon} {position}. {player.DisplayName}");
                sb.AppendLine($"   Счёт: {player.Score} | Верно: {player.CorrectAnswers} | Неверно: {player.IncorrectAnswers}");
            }
            else
            {
                // Before game starts, just show players
                sb.AppendLine($"{statusIcon} {position}. {player.DisplayName} ({statusText})");
            }

            position++;
        }

        await Client.SendTextMessageAsync(chatId, sb.ToString(), cancellationToken: cancellationToken);
    }
}

