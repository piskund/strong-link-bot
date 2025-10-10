using Microsoft.Extensions.Logging;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;

namespace StrongLink.Worker.Services;

public sealed class GameLifecycleService : IGameLifecycleService
{
    private readonly IChatMessenger _messenger;
    private readonly IGameSessionRepository _repository;
    private readonly ILocalizationService _localization;
    private readonly ILogger<GameLifecycleService> _logger;

    public GameLifecycleService(
        IChatMessenger messenger,
        IGameSessionRepository repository,
        ILocalizationService localization,
        ILogger<GameLifecycleService> logger)
    {
        _messenger = messenger;
        _repository = repository;
        _localization = localization;
        _logger = logger;
    }

    public async Task StartGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        if (session.Players.Count < 2)
        {
            var text = _localization.GetString(session.Language, "Game.NotEnoughPlayers");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        if (session.QuestionsByTour.Count == 0)
        {
            var text = _localization.GetString(session.Language, "Game.NoQuestionPool");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        session.Status = GameStatus.InProgress;
        session.StartedAt = DateTimeOffset.UtcNow;
        session.CurrentTour = 1;
        session.CurrentRound = 0;
        session.CurrentQuestion = null;
        session.TurnQueue.Clear();

        foreach (var player in session.ActivePlayers)
        {
            session.TurnQueue.Enqueue(player.Id);
        }

        await _repository.SaveAsync(session, cancellationToken);
        await AdvanceRoundAsync(session, cancellationToken);
    }

    public async Task AdvanceRoundAsync(GameSession session, CancellationToken cancellationToken)
    {
        if (!session.QuestionsByTour.TryGetValue(session.CurrentTour, out var questions) || questions.Count == 0)
        {
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        if (session.TurnQueue.Count == 0)
        {
            foreach (var activePlayer in session.ActivePlayers)
            {
                session.TurnQueue.Enqueue(activePlayer.Id);
            }

            session.CurrentRound += 1;
        }

        if (session.CurrentRound >= session.RoundsPerTour)
        {
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        if (session.TurnQueue.Count == 0)
        {
            await CompleteTourAsync(session, cancellationToken);
            return;
        }

        session.CurrentPlayerId = session.TurnQueue.Dequeue();
        session.CurrentQuestion = questions.Dequeue();
        await _repository.SaveAsync(session, cancellationToken);

        var currentPlayer = session.FindPlayer(session.CurrentPlayerId.Value);
        if (currentPlayer is null)
        {
            await AdvanceRoundAsync(session, cancellationToken);
            return;
        }

        var text = string.Format(
            _localization.GetString(session.Language, "Game.Round"),
            session.CurrentRound + 1,
            session.RoundsPerTour,
            currentPlayer.DisplayName,
            session.CurrentQuestion.Text);

        await _messenger.SendAsync(session.ChatId, text, cancellationToken);
    }

    public async Task HandleAnswerAsync(GameSession session, long playerId, string answer, CancellationToken cancellationToken)
    {
        if (session.CurrentQuestion is null || session.CurrentPlayerId is null)
        {
            return;
        }

        if (playerId != session.CurrentPlayerId.Value)
        {
            var text = _localization.GetString(session.Language, "Game.AnswerIgnored");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            return;
        }

        var player = session.FindPlayer(playerId);
        if (player is null)
        {
            await AdvanceRoundAsync(session, cancellationToken);
            return;
        }

        var normalizedAnswer = Normalize(answer);
        var normalizedCorrect = Normalize(session.CurrentQuestion.Answer);

        if (string.Equals(normalizedAnswer, normalizedCorrect, StringComparison.OrdinalIgnoreCase))
        {
            player.Score += 1;
            player.CorrectAnswers += 1;
            var text = _localization.GetString(session.Language, "Game.Correct");
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        }
        else
        {
            player.IncorrectAnswers += 1;
            var text = string.Format(
                _localization.GetString(session.Language, "Game.Incorrect"),
                session.CurrentQuestion.Answer);
            await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        }

        session.CurrentQuestion = null;
        session.CurrentPlayerId = null;
        await _repository.SaveAsync(session, cancellationToken);

        await AdvanceRoundAsync(session, cancellationToken);
    }

    public async Task StopGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        session.Status = GameStatus.Cancelled;
        session.CompletedAt = DateTimeOffset.UtcNow;
        await _repository.SaveAsync(session, cancellationToken);

        var text = _localization.GetString(session.Language, "Game.Stopped");
        await _messenger.SendAsync(session.ChatId, text, cancellationToken);
    }

    private async Task CompleteTourAsync(GameSession session, CancellationToken cancellationToken)
    {
        session.TurnQueue.Clear();
        session.CurrentRound = 0;
        session.CurrentQuestion = null;
        session.CurrentPlayerId = null;

        var eliminationCount = Math.Min(session.EliminateLowest, session.ActivePlayers.Count() - 1);
        if (eliminationCount > 0)
        {
            var toEliminate = session.ActivePlayers
                .OrderBy(p => p.Score)
                .ThenBy(p => p.IncorrectAnswers)
                .Take(eliminationCount)
                .ToList();

            foreach (var player in toEliminate)
            {
                player.Status = PlayerStatus.Eliminated;
                var text = string.Format(
                    _localization.GetString(session.Language, "Game.Eliminated"),
                    player.DisplayName);
                await _messenger.SendAsync(session.ChatId, text, cancellationToken);
            }
        }

        session.CurrentTour += 1;

        if (session.CurrentTour > session.Tours || session.ActivePlayers.Count() <= 1)
        {
            await CompleteGameAsync(session, cancellationToken);
            return;
        }

        var nextTopic = session.Topics.ElementAtOrDefault(session.CurrentTour - 1) ?? $"Topic {session.CurrentTour}";
        var textTour = string.Format(
            _localization.GetString(session.Language, "Game.TourComplete"),
            session.CurrentTour - 1,
            nextTopic);
        await _messenger.SendAsync(session.ChatId, textTour, cancellationToken);

        foreach (var player in session.ActivePlayers)
        {
            session.TurnQueue.Enqueue(player.Id);
        }

        await _repository.SaveAsync(session, cancellationToken);
        await AdvanceRoundAsync(session, cancellationToken);
    }

    private async Task CompleteGameAsync(GameSession session, CancellationToken cancellationToken)
    {
        session.Status = GameStatus.Completed;
        session.CompletedAt = DateTimeOffset.UtcNow;

        var winner = session.ActivePlayers.OrderByDescending(p => p.Score).FirstOrDefault();
        var text = winner is null
            ? _localization.GetString(session.Language, "Game.Stopped")
            : string.Format(_localization.GetString(session.Language, "Game.Completed"), winner.DisplayName);

        await _messenger.SendAsync(session.ChatId, text, cancellationToken);
        await _repository.SaveAsync(session, cancellationToken);
    }

    private static string Normalize(string value)
    {
        return value.Trim().Trim('.', '!', '?', '\'', '"');
    }
}

