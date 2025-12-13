using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Localization;
using StrongLink.Worker.Persistence;
using StrongLink.Worker.QuestionProviders;
using StrongLink.Worker.Services;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace StrongLink.Worker.Telegram.Updates.Handlers;

public sealed class PreparePoolCommandHandler : CommandHandlerBase
{
    private readonly ILogger<PreparePoolCommandHandler> _logger;
    private readonly QuestionProviderFactory _factory;
    private readonly IQuestionPoolRepository _poolRepository;

    public PreparePoolCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        QuestionProviderFactory factory,
        IQuestionPoolRepository poolRepository,
        ILogger<PreparePoolCommandHandler> logger,
        IOptions<BotOptions> botOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _factory = factory;
        _poolRepository = poolRepository;
        _logger = logger;
    }

    public override string Command => "/prepare_pool";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("User {Username} ({UserId}) issued /prepare_pool command in chat {ChatId}",
            message.From?.Username ?? "Unknown", message.From?.Id ?? 0, chatId);

        var session = await Repository.LoadAsync(chatId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("No session found for chat {ChatId}", chatId);
            return;
        }

        _logger.LogInformation("Preparing question pool for chat {ChatId}. Source: {Source}, Tours: {Tours}, Rounds: {Rounds}, Players: {PlayerCount}",
            chatId, session.QuestionSourceMode, session.Tours, session.RoundsPerTour, session.Players.Count);

        var preparing = Localization.GetString(session.Language, "Bot.PoolPreparing");
        await Client.SendTextMessageAsync(chatId, preparing, cancellationToken: cancellationToken);

        try
        {
            // Use at least 3 players when calculating required questions
            // This ensures enough questions even if players join later
            var effectivePlayerCount = Math.Max(3, session.Players.Count);
            var requiredPerTour = effectivePlayerCount * session.RoundsPerTour;
            _logger.LogInformation("Calculating required questions: {EffectiveCount} players * {Rounds} rounds = {Required} per tour",
                effectivePlayerCount, session.RoundsPerTour, requiredPerTour);
            var totalRequired = session.Tours * requiredPerTour;

            // First, try to get questions from the unused pool
            var poolStats = await _poolRepository.GetPoolStatsAsync(cancellationToken);
            _logger.LogInformation("Current pool stats: {Unused} unused, {Archived} archived",
                poolStats.Unused, poolStats.Archived);

            // Get archived questions to avoid repetition when generating new ones
            var archivedQuestions = await _poolRepository.GetArchivedQuestionsAsync(cancellationToken);
            _logger.LogInformation("Retrieved {Count} archived questions for AI context", archivedQuestions.Count);

            var pool = new Dictionary<int, List<Question>>();
            var reusedCount = 0;
            var generatedQuestions = new List<Question>();

            for (var tourIndex = 0; tourIndex < session.Tours; tourIndex++)
            {
                var topic = session.Topics.ElementAtOrDefault(tourIndex) ?? $"Topic {tourIndex + 1}";
                var questionsFromPool = await _poolRepository.SelectQuestionsAsync(topic, requiredPerTour, cancellationToken);

                if (questionsFromPool.Count >= requiredPerTour)
                {
                    // Enough questions in pool for this tour
                    pool[tourIndex + 1] = questionsFromPool.Take(requiredPerTour)
                        .Select(q => q with { Topic = topic })
                        .ToList();
                    reusedCount += requiredPerTour;
                    _logger.LogDebug("Reusing {Count} questions from pool for tour {Tour} ({Topic})",
                        requiredPerTour, tourIndex + 1, topic);
                }
                else
                {
                    // Not enough in pool, need to generate
                    _logger.LogInformation("Only {Available} questions in pool for tour {Tour}, generating {Needed} more",
                        questionsFromPool.Count, tourIndex + 1, requiredPerTour - questionsFromPool.Count);

                    var provider = _factory.Resolve(session.QuestionSourceMode);

                    // Pass archived questions to AI provider to avoid repetition
                    IReadOnlyDictionary<int, List<Question>> generated;
                    if (provider is AiQuestionProvider aiProvider)
                    {
                        generated = await aiProvider.PrepareQuestionPoolAsync(
                            new[] { topic },
                            1,
                            session.RoundsPerTour,
                            session.Players,
                            session.Language,
                            archivedQuestions,
                            cancellationToken);
                    }
                    else
                    {
                        generated = await provider.PrepareQuestionPoolAsync(
                            new[] { topic },
                            1,
                            session.RoundsPerTour,
                            session.Players,
                            session.Language,
                            cancellationToken);
                    }

                    _logger.LogInformation("Provider returned dictionary with {Count} entries", generated.Count);
                    var generatedList = generated.Values.FirstOrDefault() ?? new List<Question>();
                    _logger.LogInformation("Generated {Count} questions for tour {Tour} topic {Topic}",
                        generatedList.Count, tourIndex + 1, topic);
                    generatedQuestions.AddRange(generatedList);

                    // Combine pool questions + generated
                    var combined = new List<Question>(questionsFromPool);
                    combined.AddRange(generatedList);

                    pool[tourIndex + 1] = combined.Take(requiredPerTour).ToList();
                    _logger.LogInformation("Added {Count} questions to pool dictionary for tour {Tour}",
                        pool[tourIndex + 1].Count, tourIndex + 1);
                    reusedCount += questionsFromPool.Count;
                }
            }

            _logger.LogInformation("Pool dictionary has {Count} tours before copying to session", pool.Count);
            foreach (var (tour, questions) in pool)
            {
                _logger.LogInformation("Tour {Tour} has {Count} questions in pool dictionary", tour, questions.Count);
            }

            session.QuestionsByTour.Clear();
            foreach (var (tour, questions) in pool)
            {
                session.QuestionsByTour[tour] = new Queue<Question>(questions);
                _logger.LogInformation("Copied {Count} questions to session.QuestionsByTour[{Tour}]", questions.Count, tour);
            }

            // Add only SURPLUS generated questions back to unused pool (ones that weren't used in session)
            // Questions used in the session will be archived when the game ends
            if (generatedQuestions.Count > 0)
            {
                var usedQuestionTexts = new HashSet<string>(
                    pool.Values.SelectMany(q => q).Select(q => q.Text.Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                var surplusQuestions = generatedQuestions
                    .Where(q => !usedQuestionTexts.Contains(q.Text.Trim().ToLowerInvariant()))
                    .ToList();

                if (surplusQuestions.Count > 0)
                {
                    await _poolRepository.AddToUnusedPoolAsync(surplusQuestions, cancellationToken);
                    _logger.LogInformation("Added {Count} surplus generated questions to unused pool (out of {Total} generated)",
                        surplusQuestions.Count, generatedQuestions.Count);
                }
                else
                {
                    _logger.LogInformation("No surplus questions to add - all {Count} generated questions were used in session",
                        generatedQuestions.Count);
                }
            }

            var totalQuestions = session.QuestionsByTour.Values.Sum(q => q.Count);
            _logger.LogInformation("Question pool prepared successfully for chat {ChatId}. Total: {Total} questions ({Reused} reused, {Generated} generated)",
                chatId, totalQuestions, reusedCount, generatedQuestions.Count);

            session.Status = GameStatus.ReadyToStart;
            await Repository.SaveAsync(session, cancellationToken);

            var readyText = Localization.GetString(session.Language, "Bot.PoolReady");
            if (reusedCount > 0)
            {
                readyText += $" ({reusedCount} переиспользовано, {generatedQuestions.Count} новых)";
            }
            await Client.SendTextMessageAsync(chatId, readyText, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare question pool for chat {ChatId}", chatId);
            var failure = string.Format(Localization.GetString(session.Language, "Bot.PoolFailure"), ex.Message);
            await Client.SendTextMessageAsync(chatId, failure, cancellationToken: cancellationToken);
        }
    }
}

