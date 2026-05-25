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

/// <summary>
/// /generate — pre-fills the unused question pool for all configured topics so games
/// can start immediately without waiting for OpenAI. Runs entirely in the background.
/// Usage: /generate          — generates for all configured topics
///        /generate Фильмы   — generates for a specific topic
/// </summary>
public sealed class GeneratePoolCommandHandler : CommandHandlerBase
{
    private readonly ILogger<GeneratePoolCommandHandler> _logger;
    private readonly QuestionProviderFactory _factory;
    private readonly IQuestionPoolRepository _poolRepository;
    private readonly GameOptions _gameOptions;
    private readonly BotOptions _botOptions;

    public GeneratePoolCommandHandler(
        ITelegramBotClient client,
        ILocalizationService localization,
        IGameSessionRepository repository,
        QuestionProviderFactory factory,
        IQuestionPoolRepository poolRepository,
        ILogger<GeneratePoolCommandHandler> logger,
        IOptions<BotOptions> botOptions,
        IOptions<GameOptions> gameOptions)
        : base(client, localization, repository, botOptions.Value)
    {
        _factory = factory;
        _poolRepository = poolRepository;
        _logger = logger;
        _botOptions = botOptions.Value;
        _gameOptions = gameOptions.Value;
    }

    public override string Command => "/generate";

    protected override bool RequiresAdmin => true;

    protected override async Task HandleCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        // Parse arguments: /generate [topic] [count]
        // Examples: /generate              → all topics, 50 questions each
        //           /generate Фильмы       → one topic, 50 questions
        //           /generate Фильмы 60    → one topic, 60 questions
        //           /generate 60           → all topics, 60 questions each
        var text = message.Text ?? string.Empty;
        var rawArg = text.Length > Command.Length ? text[Command.Length..].Trim() : string.Empty;
        var parts = rawArg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int QuestionsPerTopic = 50; // safe for up to 5 players × 10 rounds
        string topicArg = string.Empty;

        if (parts.Length == 1 && int.TryParse(parts[0], out var countOnly) && countOnly > 0)
        {
            QuestionsPerTopic = countOnly;
        }
        else if (parts.Length >= 1)
        {
            topicArg = parts[0];
            if (parts.Length >= 2 && int.TryParse(parts[parts.Length - 1], out var trailingCount) && trailingCount > 0)
            {
                QuestionsPerTopic = trailingCount;
                topicArg = string.Join(" ", parts[..^1]);
            }
        }

        var language = _botOptions.DefaultLanguage;

        // Reject topic arguments that look like bot commands or internal names to prevent
        // accidental pollution of the pool with junk topics (e.g. /generate _pool).
        if (!string.IsNullOrEmpty(topicArg) && (topicArg.StartsWith('/') || topicArg.StartsWith('_')))
        {
            await Client.SendTextMessageAsync(chatId,
                language == GameLanguage.Russian
                    ? $"⚠️ Некорректное название темы: \"{topicArg}\". Используйте: /generate [тема] [количество]"
                    : $"⚠️ Invalid topic name: \"{topicArg}\". Usage: /generate [topic] [count]",
                cancellationToken: cancellationToken);
            return;
        }

        IReadOnlyList<string> topics;
        if (!string.IsNullOrEmpty(topicArg))
        {
            topics = new[] { topicArg };
        }
        else if (_gameOptions.Topics.Length > 0)
        {
            topics = _gameOptions.Topics;
        }
        else
        {
            await Client.SendTextMessageAsync(chatId,
                language == GameLanguage.Russian
                    ? "⚠️ Темы не настроены. Задайте GAME__TOPICS в .env файле."
                    : "⚠️ No topics configured. Set GAME__TOPICS in your .env file.",
                cancellationToken: cancellationToken);
            return;
        }
        var matureContent = _gameOptions.MatureContentEnabled;
        var difficultyLevel = _gameOptions.DifficultyLevel;
        var sourceMode = _botOptions.QuestionSource;

        var startMsg = language == GameLanguage.Russian
            ? $"🔄 Запускаю фоновую генерацию: {topics.Count} тем(ы), по {QuestionsPerTopic} вопросов на тему.\n" +
              $"Темы: {string.Join(", ", topics)}\nЯ сообщу когда закончу."
            : $"🔄 Starting background generation: {topics.Count} topic(s), {QuestionsPerTopic} questions each.\n" +
              $"Topics: {string.Join(", ", topics)}\nI'll notify you when done.";

        await Client.SendTextMessageAsync(chatId, startMsg, cancellationToken: cancellationToken);

        _ = Task.Run(async () =>
        {
            var totalAdded = 0;
            var failed = new List<string>();

            try
            {
                foreach (var topic in topics)
                {
                    try
                    {
                        _logger.LogInformation("/generate: generating {Count} questions for topic '{Topic}'",
                            QuestionsPerTopic, topic);

                        var archived = await _poolRepository.GetArchivedQuestionsByTopicAllTimeAsync(topic, CancellationToken.None);

                        var provider = _factory.Resolve(sourceMode);
                        IReadOnlyDictionary<int, List<Question>> generated;

                        if (provider is AiQuestionProvider aiProvider)
                        {
                            generated = await aiProvider.PrepareQuestionPoolAsync(
                                new[] { topic }, 1, QuestionsPerTopic,
                                new List<Player>(), // no players yet — question count = QuestionsPerTopic directly
                                language, matureContent,
                                archived.TakeLast(100).ToList(),
                                difficultyLevel,
                                CancellationToken.None);
                        }
                        else
                        {
                            generated = await provider.PrepareQuestionPoolAsync(
                                new[] { topic }, 1, QuestionsPerTopic,
                                new List<Player>(),
                                language, matureContent,
                                CancellationToken.None);
                        }

                        var list = generated.Values.FirstOrDefault() ?? new List<Question>();
                        if (list.Count > 0)
                        {
                            await _poolRepository.AddToUnusedPoolAsync(list, CancellationToken.None);
                            totalAdded += list.Count;
                            _logger.LogInformation("/generate: added {Count} questions for topic '{Topic}'", list.Count, topic);
                        }
                        else
                        {
                            failed.Add(topic);
                            _logger.LogWarning("/generate: no questions returned for topic '{Topic}'", topic);
                        }

                        // Brief pause between topics to avoid immediately re-hitting rate limits
                        if (topics.Count > 1)
                            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(topic);
                        _logger.LogError(ex, "/generate: failed for topic '{Topic}'", topic);
                    }
                }

                var poolStats = await _poolRepository.GetPoolStatsAsync(CancellationToken.None);

                var doneMsg = language == GameLanguage.Russian
                    ? $"✅ Генерация завершена! Добавлено {totalAdded} вопросов.\n" +
                      $"Всего в пуле: {poolStats.Unused} неиспользованных вопросов." +
                      (failed.Count > 0 ? $"\n⚠️ Не удалось: {string.Join(", ", failed)}" : "")
                    : $"✅ Generation complete! Added {totalAdded} questions.\n" +
                      $"Pool total: {poolStats.Unused} unused questions." +
                      (failed.Count > 0 ? $"\n⚠️ Failed topics: {string.Join(", ", failed)}" : "");

                await Client.SendTextMessageAsync(chatId, doneMsg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "/generate: background task failed");
                await Client.SendTextMessageAsync(chatId,
                    language == GameLanguage.Russian
                        ? "❌ Ошибка при генерации вопросов."
                        : "❌ Generation failed.");
            }
        });
    }
}
