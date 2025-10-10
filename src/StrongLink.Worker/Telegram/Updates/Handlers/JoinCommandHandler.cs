using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class JoinCommandHandler : CommandHandlerBase
{
    public JoinCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository)
        : base(client, localization, repository)
    {
    }

    public override string Command => "/join";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            return;
        }

        var from = message.From;
        if (from is null)
        {
            return;
        }

        var language = session.Language;
        var existing = session.FindPlayer(from.Id);
        if (existing is not null)
        {
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

        session.Players.Add(player);
        session.TurnQueue.Enqueue(player.Id);
        await Repository.SaveAsync(session, cancellationToken);

        var joinedText = string.Format(Localization.GetString(language, "Bot.Joined"), player.DisplayName);
        await Client.SendTextMessageAsync(chatId, joinedText, cancellationToken: cancellationToken);
    }
}

