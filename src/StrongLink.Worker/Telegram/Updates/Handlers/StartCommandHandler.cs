using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class StartCommandHandler : CommandHandlerBase
{
    private readonly ILogger<StartCommandHandler> _logger;
    private readonly BotOptions _botOptions;
    private readonly GameOptions _gameOptions;
    public StartCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        ILogger<StartCommandHandler> logger,
        IOptions<BotOptions> botOptions,
        IOptions<GameOptions> gameOptions)
        : base(client, localization, repository)
    {
        _logger = logger;
        _botOptions = botOptions.Value;
        _gameOptions = gameOptions.Value;
    }

    public override string Command => "/start";

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type is not (ChatType.Group or ChatType.Supergroup))
        {
            await Client.SendTextMessageAsync(message.Chat.Id, "This bot works in group chats only.", cancellationToken: cancellationToken);
            return;
        }

        var chatId = message.Chat.Id;
        var session = await Repository.LoadAsync(chatId, cancellationToken) ?? new GameSession
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

        if (session.Status == GameStatus.InProgress)
        {
            var text = Localization.GetString(session.Language, "Bot.GameAlreadyRunning");
            await Client.SendTextMessageAsync(chatId, text, cancellationToken: cancellationToken);
            return;
        }

        session.Status = GameStatus.AwaitingPlayers;
        await Repository.SaveAsync(session, cancellationToken);

        var welcome = Localization.GetString(session.Language, "Bot.Welcome");
        var help = Localization.GetString(session.Language, "Bot.Help");
        await Client.SendTextMessageAsync(chatId, $"{welcome}\n{help}", cancellationToken: cancellationToken);
    }
}

