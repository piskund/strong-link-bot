using System.Text.Json;
using Microsoft.Extensions.Options;
using StrongLink.Worker.Configuration;
using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Persistence;

public interface IQuestionPoolRepository
{
    Task<List<Question>> GetUnusedQuestionsAsync(CancellationToken cancellationToken = default);
    Task<List<Question>> GetArchivedQuestionsAsync(CancellationToken cancellationToken = default);
    Task<List<Question>> GetArchivedQuestionsByTopicAsync(string topic, int maxMonthsBack = 1, CancellationToken cancellationToken = default);
    Task<List<Question>> GetArchivedQuestionsByTopicAllTimeAsync(string topic, CancellationToken cancellationToken = default);
    Task AddToUnusedPoolAsync(IEnumerable<Question> questions, CancellationToken cancellationToken = default);
    Task MoveToArchiveAsync(IEnumerable<Question> questions, CancellationToken cancellationToken = default);
    Task<(int Unused, int Archived)> GetPoolStatsAsync(CancellationToken cancellationToken = default);
    Task ClearPoolAsync(bool clearArchive, CancellationToken cancellationToken = default);
    Task<List<Question>> SelectQuestionsAsync(string topic, int count, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetAvailableTopicsAsync(CancellationToken cancellationToken = default);
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

    public async Task<List<Question>> GetArchivedQuestionsByTopicAsync(
        string topic,
        int maxMonthsBack = 1,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var sanitizedTopic = SanitizeTopicName(topic);
            var allQuestions = new List<Question>();

            if (!Directory.Exists(_archivedPath))
            {
                return allQuestions;
            }

            // Calculate the date range to search (go back X months from current month)
            var currentMonth = DateTimeOffset.UtcNow;
            var targetMonths = new List<string>();

            for (int i = 0; i <= maxMonthsBack; i++)
            {
                var monthDate = currentMonth.AddMonths(-i);
                targetMonths.Add(monthDate.ToString("yyyy-MM"));
            }

            _logger.LogDebug("Searching for archived questions for topic '{Topic}' in months: {Months}",
                topic, string.Join(", ", targetMonths));

            // Load questions from target months
            foreach (var month in targetMonths)
            {
                var monthPath = Path.Combine(_archivedPath, month);
                if (!Directory.Exists(monthPath))
                {
                    continue;
                }

                // Load the specific topic file
                var topicFile = Path.Combine(monthPath, $"{sanitizedTopic}.json");
                if (File.Exists(topicFile))
                {
                    var questions = await LoadQuestionsFromFileAsync(topicFile, cancellationToken);
                    allQuestions.AddRange(questions);
                    _logger.LogDebug("Loaded {Count} questions from {Month}/{Topic}",
                        questions.Count, month, sanitizedTopic);
                }
            }

            _logger.LogInformation("Loaded {Count} archived questions for topic '{Topic}' from last {Months} month(s)",
                allQuestions.Count, topic, maxMonthsBack + 1);

            return allQuestions;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<Question>> GetArchivedQuestionsByTopicAllTimeAsync(
        string topic,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var sanitizedTopic = SanitizeTopicName(topic);
            var allQuestions = new List<Question>();

            if (!Directory.Exists(_archivedPath))
            {
                return allQuestions;
            }

            var monthFolders = Directory.GetDirectories(_archivedPath)
                .OrderBy(d => d)
                .ToArray();

            foreach (var monthFolder in monthFolders)
            {
                var topicFile = Path.Combine(monthFolder, $"{sanitizedTopic}.json");
                if (File.Exists(topicFile))
                {
                    var questions = await LoadQuestionsFromFileAsync(topicFile, cancellationToken);
                    allQuestions.AddRange(questions);
                    _logger.LogDebug("Loaded {Count} questions from {Month}/{Topic}",
                        questions.Count, Path.GetFileName(monthFolder), sanitizedTopic);
                }
            }

            _logger.LogInformation("Loaded {Count} archived questions (all time) for topic '{Topic}' from {FolderCount} month folders",
                allQuestions.Count, topic, monthFolders.Length);

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

                var existingAnswers = new HashSet<string>(
                    existing.Select(q => NormalizeText(q.Answer)),
                    StringComparer.OrdinalIgnoreCase);

                var toAdd = topicGroup
                    .Where(q => !existingTexts.Contains(NormalizeText(q.Text))
                             && !existingAnswers.Contains(NormalizeText(q.Answer)))
                    .ToList();

                // Also dedup within the incoming batch itself (same answer, different wording)
                var deduped = new List<Question>();
                var seenAnswers = new HashSet<string>(existingAnswers, StringComparer.OrdinalIgnoreCase);
                var seenTexts = new HashSet<string>(existingTexts, StringComparer.OrdinalIgnoreCase);
                foreach (var q in toAdd)
                {
                    var answerKey = NormalizeText(q.Answer);
                    var textKey = NormalizeText(q.Text);
                    if (!seenAnswers.Contains(answerKey) && !seenTexts.Contains(textKey))
                    {
                        deduped.Add(q);
                        seenAnswers.Add(answerKey);
                        seenTexts.Add(textKey);
                    }
                }

                if (deduped.Count > 0)
                {
                    existing.AddRange(deduped);
                    await SaveQuestionsToFileAsync(topicFile, existing, cancellationToken);
                    addedCount += deduped.Count;
                    _logger.LogDebug("Added {Count} questions to topic '{Topic}'", deduped.Count, topic);
                }

                duplicateCount += topicGroup.Count() - deduped.Count;
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

            // Load questions from topic-specific file only — no cross-topic fallback
            var topicQuestions = await LoadQuestionsFromFileAsync(topicFile, cancellationToken);

            var selected = topicQuestions.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();

            if (selected.Count > 0)
            {
                var selectedTexts = new HashSet<string>(
                    selected.Select(q => NormalizeText(q.Text)),
                    StringComparer.OrdinalIgnoreCase);

                var remaining = topicQuestions
                    .Where(q => !selectedTexts.Contains(NormalizeText(q.Text)))
                    .ToList();
                await SaveQuestionsToFileAsync(topicFile, remaining, cancellationToken);

                _logger.LogInformation("Selected {Count} questions for topic '{Topic}'", selected.Count, topic);
            }

            return selected;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Dictionary<string, int>> GetAvailableTopicsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var topicCounts = new Dictionary<string, int>();
            var topicFiles = Directory.GetFiles(_unusedPath, "*.json");

            foreach (var file in topicFiles)
            {
                var questions = await LoadQuestionsFromFileAsync(file, cancellationToken);
                if (questions.Count > 0)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // Use the actual topic from questions if available, otherwise use filename
                    var topicName = questions.FirstOrDefault()?.Topic ?? fileName;
                    topicCounts[topicName] = questions.Count;
                }
            }

            _logger.LogDebug("Found {Count} topics with unused questions", topicCounts.Count);
            return topicCounts;
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
