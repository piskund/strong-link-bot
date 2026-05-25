using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class KickCommandHandler : CommandHandlerBase
{
    private readonly ILogger<KickCommandHandler> _logger;
    private readonly IGameLifecycleService _lifecycleService;

    public KickCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        IGameLifecycleService lifecycleService,
        ILogger<KickCommandHandler> logger,
        IOptions<BotOptions> botOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _lifecycleService = lifecycleService;
        _logger = logger;
    }

    public override string Command => "/kick";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var session = await Repository.LoadAsync(chatId, cancellationToken);

        if (session is null || session.Status == GameStatus.Completed || session.Status == GameStatus.Cancelled)
        {
            var noGame = session?.Language == GameLanguage.English
                ? "No active game session."
                : "Нет активной игровой сессии.";
            await Client.SendTextMessageAsync(chatId, noGame, cancellationToken: cancellationToken);
            return;
        }

        // Parse target: /kick @username or /kick firstname
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length < 2)
        {
            var usage = session.Language == GameLanguage.English
                ? "Usage: /kick @username or /kick firstname"
                : "Использование: /kick @username или /kick имя";
            await Client.SendTextMessageAsync(chatId, usage, cancellationToken: cancellationToken);
            return;
        }

        var target = string.Join(" ", parts.Skip(1)).Trim();
        var targetLower = target.TrimStart('@').ToLowerInvariant();

        var player = session.Players.FirstOrDefault(p =>
            p.DisplayName.TrimStart('@').Equals(targetLower, StringComparison.OrdinalIgnoreCase));

        if (player is null)
        {
            var notFound = session.Language == GameLanguage.English
                ? $"Player \"{target}\" not found in this game."
                : $"Игрок \"{target}\" не найден в этой игре.";
            await Client.SendTextMessageAsync(chatId, notFound, cancellationToken: cancellationToken);
            return;
        }

        if (player.Status == PlayerStatus.Eliminated)
        {
            var alreadyOut = session.Language == GameLanguage.English
                ? $"{player.DisplayName} is already eliminated."
                : $"{player.DisplayName} уже выбыл из игры.";
            await Client.SendTextMessageAsync(chatId, alreadyOut, cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Admin kicked player {PlayerName} ({PlayerId}) from chat {ChatId}. Game status: {Status}",
            player.DisplayName, player.Id, chatId, session.Status);

        var isCurrentPlayer = session.CurrentPlayerId == player.Id;

        player.Status = PlayerStatus.Eliminated;

        var kickedText = session.Language == GameLanguage.English
            ? $"👢 {player.DisplayName} has been removed from the game by the admin."
            : $"👢 {player.DisplayName} удалён из игры администратором.";
        await Client.SendTextMessageAsync(chatId, kickedText, cancellationToken: cancellationToken);

        var activePlayers = session.ActivePlayers.ToList();

        // Game over if nobody left
        if (activePlayers.Count == 0)
        {
            await Repository.SaveAsync(session, cancellationToken);
            await _lifecycleService.StopGameAsync(session, cancellationToken);
            return;
        }

        // If not in progress, just save
        if (session.Status != GameStatus.InProgress && session.Status != GameStatus.SuddenDeath)
        {
            // Remove from turn queue if present
            var newQueue = new Queue<long>(session.TurnQueue.Where(id => id != player.Id));
            session.TurnQueue.Clear();
            foreach (var id in newQueue) session.TurnQueue.Enqueue(id);

            await Repository.SaveAsync(session, cancellationToken);
            return;
        }

        // Remove kicked player from turn queue
        var filteredQueue = new Queue<long>(session.TurnQueue.Where(id => id != player.Id));
        session.TurnQueue.Clear();
        foreach (var id in filteredQueue) session.TurnQueue.Enqueue(id);

        await Repository.SaveAsync(session, cancellationToken);

        // If it was their turn, skip to the next round
        if (isCurrentPlayer)
        {
            session.CurrentQuestion = null;
            session.CurrentPlayerId = null;
            session.CurrentQuestionAskedAt = null;
            session.CurrentQuestionMessageId = null;
            await Repository.SaveAsync(session, cancellationToken);
            await _lifecycleService.AdvanceRoundAsync(session, cancellationToken);
        }
    }
}
