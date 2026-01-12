using StrongLink.Worker.Persistence;

namespace StrongLink.Worker.Services;

/// <summary>
/// Service for smart topic selection that prioritizes using topics with available unused questions.
/// Configured topics are treated as recommendations, not mandatory requirements.
/// </summary>
public static class TopicSelector
{
    /// <summary>
    /// Selects topics for a game session, prioritizing topics with unused questions available.
    /// This reduces question generation time, API costs, and avoids wasting pre-generated questions.
    /// </summary>
    /// <param name="poolRepository">Repository to check for available unused questions</param>
    /// <param name="configuredTopics">Recommended topics from configuration (not mandatory)</param>
    /// <param name="requiredCount">Number of topics needed</param>
    /// <param name="logger">Logger for diagnostics</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of selected topics, prioritizing those with available questions</returns>
    public static async Task<List<string>> SelectOptimalTopicsAsync(
        IQuestionPoolRepository poolRepository,
        string[] configuredTopics,
        int requiredCount,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Handle null or empty configured topics
        if (configuredTopics == null || configuredTopics.Length == 0)
        {
            logger.LogWarning("No configured topics provided. Using default topic 'General'.");
            configuredTopics = new[] { "General" };
        }

        // Get available topics with their question counts from the unused pool
        var availableTopics = await poolRepository.GetAvailableTopicsAsync(cancellationToken);

        if (availableTopics == null || availableTopics.Count == 0)
        {
            // No unused questions available - use configured topics shuffled
            logger.LogDebug("No unused questions available. Using configured topics.");
            return ShuffleTopics(configuredTopics, requiredCount);
        }

        // Sort available topics by question count (descending) to prioritize topics with most questions
        var topicsByCount = availableTopics
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();

        logger.LogInformation("Found {Count} topics with unused questions. Top 5: {Topics}",
            availableTopics.Count,
            string.Join(", ", topicsByCount.Take(5).Select(t => $"{t} ({availableTopics[t]})")));

        var selectedTopics = new List<string>();

        // Phase 1: Prioritize topics with most unused questions
        // Take topics that have unused questions, up to requiredCount
        foreach (var topic in topicsByCount)
        {
            if (selectedTopics.Count >= requiredCount)
                break;

            selectedTopics.Add(topic);
            logger.LogDebug("Selected topic '{Topic}' with {Count} unused questions",
                topic, availableTopics[topic]);
        }

        // Phase 2: If we still need more topics, add from configured topics (shuffled)
        if (selectedTopics.Count < requiredCount)
        {
            var remainingNeeded = requiredCount - selectedTopics.Count;
            logger.LogDebug("Need {Count} more topics. Adding from configured topics.",
                remainingNeeded);

            var unusedConfigured = configuredTopics
                .Where(ct => !selectedTopics.Contains(ct, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // Shuffle and take what we need
            var shuffled = ShuffleList(unusedConfigured);
            selectedTopics.AddRange(shuffled.Take(remainingNeeded));
        }

        // Phase 3: Shuffle the final list to mix pool topics with configured topics
        // This provides variety while still prioritizing question reuse
        var finalTopics = ShuffleList(selectedTopics);

        logger.LogInformation("Selected {Count} topics for game: {Topics}",
            finalTopics.Count,
            string.Join(", ", finalTopics));

        return finalTopics;
    }

    private static List<string> ShuffleTopics(string[] topics, int count)
    {
        var list = topics.ToList();
        return ShuffleList(list).Take(count).ToList();
    }

    private static List<T> ShuffleList<T>(List<T> list)
    {
        var shuffled = list.ToList();
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled;
    }
}
