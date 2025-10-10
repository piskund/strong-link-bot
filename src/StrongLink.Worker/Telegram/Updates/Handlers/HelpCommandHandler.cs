using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class HelpCommandHandler : CommandHandlerBase
{
    public HelpCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository)
        : base(client, localization, repository)
    {
    }

    public override string Command => "/help";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var session = await Repository.LoadAsync(message.Chat.Id, cancellationToken);
        var language = session?.Language ?? Domain.GameLanguage.Russian;
        var help = Localization.GetString(language, "Bot.Help");
        await Client.SendTextMessageAsync(message.Chat.Id, help, cancellationToken: cancellationToken);
    }
}

