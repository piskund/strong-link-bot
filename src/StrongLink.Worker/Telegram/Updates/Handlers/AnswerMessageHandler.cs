using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class AnswerMessageHandler : IUpdateHandler
{
    private readonly ITelegramBotClient _client;
    private readonly IGameSessionRepository _repository;
    private readonly IGameLifecycleService _lifecycleService;

    public AnswerMessageHandler(
        ITelegramBotClient client,
        IGameSessionRepository repository,
        IGameLifecycleService lifecycleService)
    {
        _client = client;
        _repository = repository;
        _lifecycleService = lifecycleService;
    }

    public bool CanHandle(Update update)
    {
        return update.Message?.Chat.Type is ChatType.Group or ChatType.Supergroup
               && update.Message?.Text is not null
               && !update.Message.Text.StartsWith("/");
    }

    public async Task HandleAsync(Update update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message?.From is null)
        {
            return;
        }

        var session = await _repository.LoadAsync(message.Chat.Id, cancellationToken);
        if (session is null || (session.Status != Domain.GameStatus.InProgress && session.Status != Domain.GameStatus.SuddenDeath))
        {
            return;
        }

        // Only accept answers from the current player
        if (session.CurrentPlayerId is null || message.From.Id != session.CurrentPlayerId.Value)
        {
            // Ignore messages from other players
            return;
        }

        // Only accept answers that are replies to the question message
        if (session.CurrentQuestionMessageId.HasValue &&
            message.ReplyToMessage?.MessageId != session.CurrentQuestionMessageId.Value)
        {
            // Ignore messages that are not replies to the current question
            return;
        }

        await _lifecycleService.HandleAnswerAsync(session, message.From.Id, message.Text ?? string.Empty, cancellationToken);
    }
}

