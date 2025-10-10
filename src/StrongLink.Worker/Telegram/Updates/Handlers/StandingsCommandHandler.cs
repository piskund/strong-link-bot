using System.Text;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class StandingsCommandHandler : CommandHandlerBase
{
    public StandingsCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository)
        : base(client, localization, repository)
    {
    }

    public override string Command => "/standings";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var session = await Repository.LoadAsync(message.Chat.Id, cancellationToken);
        if (session is null)
        {
            return;
        }

        var language = session.Language;
        if (session.Players.Count == 0)
        {
            var text = Localization.GetString(language, "Bot.NoPlayers");
            await Client.SendTextMessageAsync(message.Chat.Id, text, cancellationToken: cancellationToken);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(Localization.GetString(language, "Game.StandingsHeader"));

        var ranking = session.Players
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.IncorrectAnswers)
            .ToList();

        var position = 1;
        foreach (var player in ranking)
        {
            var status = player.Status switch
            {
                Domain.PlayerStatus.Active => language == Domain.GameLanguage.Russian ? "(в игре)" : "(in play)",
                Domain.PlayerStatus.Eliminated => language == Domain.GameLanguage.Russian ? "(выбыл)" : "(eliminated)",
                _ => string.Empty
            };

            sb.AppendLine($"{position}. {player.DisplayName} — {player.Score} {status}");
            position++;
        }

        await Client.SendTextMessageAsync(message.Chat.Id, sb.ToString(), cancellationToken: cancellationToken);
    }
}

