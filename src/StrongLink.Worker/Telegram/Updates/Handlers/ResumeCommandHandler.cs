using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class ResumeCommandHandler : CommandHandlerBase
{
    private readonly IGameLifecycleService _lifecycleService;
    private readonly ILogger<ResumeCommandHandler> _logger;

    public ResumeCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IGameLifecycleService lifecycleService,
        ILogger<ResumeCommandHandler> logger,
        IOptions<BotOptions> botOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _lifecycleService = lifecycleService;
        _logger = logger;
    }

    public override string Command => "/resume";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("User {Username} ({UserId}) issued /resume command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, message.Chat.Id);

        var session = await Repository.LoadAsync(message.Chat.Id, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}", message.Chat.Id);
            return;
        }

        await _lifecycleService.ResumeGameAsync(session, cancellationToken);
    }
}
