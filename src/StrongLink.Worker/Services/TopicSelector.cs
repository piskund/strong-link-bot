using StrongLink.Worker.Persistence;

namespace StrongLink.Worker.Services;

public static class TopicSelector
{
    public static async Task<List<string>> SelectOptimalTopicsAsync(
        IQuestionPoolRepository poolRepository,
        string[] configuredTopics,
        int requiredCount,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (configuredTopics == null || configuredTopics.Length == 0)
        {
            logger.LogWarning("No configured topics provided. Using default topic 'General'.");
            configuredTopics = new[] { "General" };
        }

        var availableTopics = await poolRepository.GetAvailableTopicsAsync(cancellationToken);
        var poolCounts = availableTopics ?? new Dictionary<string, int>();

        // Shuffle all configured topics randomly first, then stable-sort so topics with
        // pool questions come before topics without — but random order within each group.
        var shuffled = ShuffleList(configuredTopics.ToList());
        var selected = shuffled
            .OrderByDescending(t => poolCounts.TryGetValue(t, out var c) && c > 0 ? 1 : 0)
            .Take(requiredCount)
            .ToList();

        // Final shuffle so pool-rich topics don't always land first in the game order.
        var final = ShuffleList(selected);

        logger.LogInformation("Selected {Count} topics for game: {Topics}",
            final.Count,
            string.Join(", ", final.Select(t => poolCounts.TryGetValue(t, out var c) ? $"{t}({c})" : t)));

        return final;
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
