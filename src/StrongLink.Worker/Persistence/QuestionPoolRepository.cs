using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Persistence;

public interface IQuestionPoolRepository
{
    Task<List<Question>> GetUnusedQuestionsAsync(CancellationToken cancellationToken = default);
    Task<List<Question>> GetArchivedQuestionsAsync(CancellationToken cancellationToken = default);
    Task AddToUnusedPoolAsync(IEnumerable<Question> questions, CancellationToken cancellationToken = default);
    Task MoveToArchiveAsync(IEnumerable<Question> questions, CancellationToken cancellationToken = default);
    Task<(int Unused, int Archived)> GetPoolStatsAsync(CancellationToken cancellationToken = default);
    Task ClearPoolAsync(bool clearArchive, CancellationToken cancellationToken = default);
    Task<List<Question>> SelectQuestionsAsync(string topic, int count, CancellationToken cancellationToken = default);
}

public sealed class QuestionPoolRepository : IQuestionPoolRepository
{
    private readonly ILogger<QuestionPoolRepository> _logger;
    private readonly BotOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _poolPath;
    private readonly string _unusedPath;
    private readonly string _archivedPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public QuestionPoolRepository(
        ILogger<QuestionPoolRepository> logger,
        IOptions<BotOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _poolPath = Path.Combine(_options.StateStoragePath, "pools");
        _unusedPath = Path.Combine(_poolPath, "unused");
        _archivedPath = Path.Combine(_poolPath, "archived");

        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        Directory.CreateDirectory(_unusedPath);
        Directory.CreateDirectory(_archivedPath);
    }

    public async Task<List<Question>> GetUnusedQuestionsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var allQuestions = new List<Question>();
            var topicFiles = Directory.GetFiles(_unusedPath, "*.json");

            foreach (var file in topicFiles)
            {
                var questions = await LoadQuestionsFromFileAsync(file, cancellationToken);
                allQuestions.AddRange(questions);
            }

            _logger.LogDebug("Loaded {Count} unused questions from {FileCount} topic files",
                allQuestions.Count, topicFiles.Length);

            return allQuestions;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<Question>> GetArchivedQuestionsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var allQuestions = new List<Question>();
            var monthFolders = Directory.Exists(_archivedPath)
                ? Directory.GetDirectories(_archivedPath)
                : Array.Empty<string>();

            foreach (var monthFolder in monthFolders)
            {
                var topicFiles = Directory.GetFiles(monthFolder, "*.json");
                foreach (var file in topicFiles)
                {
                    var questions = await LoadQuestionsFromFileAsync(file, cancellationToken);
                    allQuestions.AddRange(questions);
                }
            }

            _logger.LogDebug("Loaded {Count} archived questions from {FolderCount} month folders",
                allQuestions.Count, monthFolders.Length);

            return allQuestions;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddToUnusedPoolAsync(IEnumerable<Question> questions, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var questionsList = questions.ToList();
            if (questionsList.Count == 0)
            {
                return;
            }

            // Group questions by topic
            var byTopic = questionsList.GroupBy(q => SanitizeTopicName(q.Topic));

            var addedCount = 0;
            var duplicateCount = 0;

            foreach (var topicGroup in byTopic)
            {
                var topic = topicGroup.Key;
                var topicFile = GetUnusedTopicFilePath(topic);

                // Load existing questions for this topic
                var existing = await LoadQuestionsFromFileAsync(topicFile, cancellationToken);

                // Deduplicate
                var existingTexts = new HashSet<string>(
                    existing.Select(q => NormalizeText(q.Text)),
                    StringComparer.OrdinalIgnoreCase);

                var toAdd = topicGroup
                    .Where(q => !existingTexts.Contains(NormalizeText(q.Text)))
                    .ToList();

                if (toAdd.Count > 0)
                {
                    existing.AddRange(toAdd);
                    await SaveQuestionsToFileAsync(topicFile, existing, cancellationToken);
                    addedCount += toAdd.Count;
                    _logger.LogDebug("Added {Count} questions to topic '{Topic}'", toAdd.Count, topic);
                }

                duplicateCount += topicGroup.Count() - toAdd.Count;
            }

            _logger.LogInformation("Added {Added} new questions to unused pool across {Topics} topics (skipped {Duplicates} duplicates)",
                addedCount, byTopic.Count(), duplicateCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MoveToArchiveAsync(IEnumerable<Question> questions, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var questionsList = questions.ToList();
            if (questionsList.Count == 0)
            {
                return;
            }

            // Group questions by topic for processing
            var byTopic = questionsList.GroupBy(q => SanitizeTopicName(q.Topic));

            var currentMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM");
            var archiveMonthPath = Path.Combine(_archivedPath, currentMonth);
            Directory.CreateDirectory(archiveMonthPath);

            var archivedCount = 0;

            foreach (var topicGroup in byTopic)
            {
                var topic = topicGroup.Key;
                var unusedFile = GetUnusedTopicFilePath(topic);
                var archivedFile = Path.Combine(archiveMonthPath, $"{topic}.json");

                // Load existing questions
                var unused = await LoadQuestionsFromFileAsync(unusedFile, cancellationToken);
                var archived = await LoadQuestionsFromFileAsync(archivedFile, cancellationToken);

                // Remove from unused pool
                var questionsToRemove = new HashSet<string>(
                    topicGroup.Select(q => NormalizeText(q.Text)),
                    StringComparer.OrdinalIgnoreCase);

                var remainingUnused = unused
                    .Where(q => !questionsToRemove.Contains(NormalizeText(q.Text)))
                    .ToList();

                // Add to archive (deduplicate)
                var archivedTexts = new HashSet<string>(
                    archived.Select(q => NormalizeText(q.Text)),
                    StringComparer.OrdinalIgnoreCase);

                var toArchive = topicGroup
                    .Where(q => !archivedTexts.Contains(NormalizeText(q.Text)))
                    .ToList();

                if (toArchive.Count > 0)
                {
                    archived.AddRange(toArchive);
                    await SaveQuestionsToFileAsync(archivedFile, archived, cancellationToken);
                    archivedCount += toArchive.Count;
                }

                // Save remaining unused
                await SaveQuestionsToFileAsync(unusedFile, remainingUnused, cancellationToken);

                _logger.LogDebug("Topic '{Topic}': archived {Archived}, remaining unused {Remaining}",
                    topic, toArchive.Count, remainingUnused.Count);
            }

            _logger.LogInformation("Archived {Count} questions to {Month}", archivedCount, currentMonth);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(int Unused, int Archived)> GetPoolStatsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Call internal methods to avoid deadlock (already have the lock)
            var unusedCount = 0;
            var topicFiles = Directory.GetFiles(_unusedPath, "*.json");
            foreach (var file in topicFiles)
            {
                var questions = await LoadQuestionsFromFileAsync(file, cancellationToken);
                unusedCount += questions.Count;
            }

            var archivedCount = 0;
            var monthFolders = Directory.Exists(_archivedPath)
                ? Directory.GetDirectories(_archivedPath)
                : Array.Empty<string>();
            foreach (var monthFolder in monthFolders)
            {
                var monthFiles = Directory.GetFiles(monthFolder, "*.json");
                foreach (var file in monthFiles)
                {
                    var questions = await LoadQuestionsFromFileAsync(file, cancellationToken);
                    archivedCount += questions.Count;
                }
            }

            return (unusedCount, archivedCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearPoolAsync(bool clearArchive, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Clear unused pool
            if (Directory.Exists(_unusedPath))
            {
                var files = Directory.GetFiles(_unusedPath, "*.json");
                foreach (var file in files)
                {
                    File.Delete(file);
                }
                _logger.LogInformation("Cleared {Count} unused topic files", files.Length);
            }

            // Clear archive if requested
            if (clearArchive && Directory.Exists(_archivedPath))
            {
                var monthFolders = Directory.GetDirectories(_archivedPath);
                foreach (var folder in monthFolders)
                {
                    Directory.Delete(folder, true);
                }
                _logger.LogInformation("Cleared {Count} archived month folders", monthFolders.Length);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<Question>> SelectQuestionsAsync(string topic, int count, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var sanitizedTopic = SanitizeTopicName(topic);
            var topicFile = GetUnusedTopicFilePath(sanitizedTopic);
            var generalFile = GetUnusedTopicFilePath("General");

            // Load questions from topic-specific file
            var topicQuestions = await LoadQuestionsFromFileAsync(topicFile, cancellationToken);

            // Randomize topic questions
            var shuffledTopicQuestions = topicQuestions.OrderBy(_ => Random.Shared.Next()).ToList();

            // If not enough, add from General pool
            if (shuffledTopicQuestions.Count < count)
            {
                var generalQuestions = await LoadQuestionsFromFileAsync(generalFile, cancellationToken);
                var shuffledGeneralQuestions = generalQuestions.OrderBy(_ => Random.Shared.Next()).ToList();
                shuffledTopicQuestions.AddRange(shuffledGeneralQuestions);
            }

            // Take requested count
            var selected = shuffledTopicQuestions.Take(count).ToList();

            // Remove selected questions from their respective files
            if (selected.Count > 0)
            {
                var selectedTexts = new HashSet<string>(
                    selected.Select(q => NormalizeText(q.Text)),
                    StringComparer.OrdinalIgnoreCase);

                // Remove from topic file
                var remainingTopic = topicQuestions
                    .Where(q => !selectedTexts.Contains(NormalizeText(q.Text)))
                    .ToList();
                await SaveQuestionsToFileAsync(topicFile, remainingTopic, cancellationToken);

                // Also check general file if we used it
                if (shuffledTopicQuestions.Count > topicQuestions.Count)
                {
                    var generalQuestions = await LoadQuestionsFromFileAsync(generalFile, cancellationToken);
                    var remainingGeneral = generalQuestions
                        .Where(q => !selectedTexts.Contains(NormalizeText(q.Text)))
                        .ToList();
                    await SaveQuestionsToFileAsync(generalFile, remainingGeneral, cancellationToken);
                }

                _logger.LogInformation("Selected {Count} questions for topic '{Topic}' (topic: {TopicCount}, general: {GeneralCount})",
                    selected.Count, topic,
                    topicQuestions.Count - remainingTopic.Count,
                    selected.Count - (topicQuestions.Count - remainingTopic.Count));
            }

            return selected;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<Question>> LoadQuestionsFromFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new List<Question>();
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var questions = await JsonSerializer.DeserializeAsync<List<Question>>(
                stream,
                _serializerOptions,
                cancellationToken);

            return questions ?? new List<Question>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load questions from {Path}", filePath);
            return new List<Question>();
        }
    }

    private async Task SaveQuestionsToFileAsync(string filePath, List<Question> questions, CancellationToken cancellationToken)
    {
        // If no questions, delete the file instead of creating an empty one
        if (questions.Count == 0)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogDebug("Deleted empty question file: {Path}", filePath);
            }
            return;
        }

        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(questions, _serializerOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private string GetUnusedTopicFilePath(string topic)
    {
        return Path.Combine(_unusedPath, $"{topic}.json");
    }

    private static string SanitizeTopicName(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return "General";
        }

        // Remove invalid file name characters
        var sanitized = topic.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "General" : sanitized;
    }

    private static string NormalizeText(string text)
    {
        return text.Trim().Trim('.', '!', '?', '\'', '"').ToLowerInvariant();
    }
}
