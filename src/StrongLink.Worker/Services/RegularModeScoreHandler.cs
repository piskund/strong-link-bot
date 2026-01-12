using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

/// <summary>
/// Handles scoring for regular game mode.
/// Updates the main Score field and uses standard question thresholds.
/// </summary>
public sealed class RegularModeScoreHandler : IGameModeScoreHandler
{
    private readonly ILogger<RegularModeScoreHandler> _logger;

    public RegularModeScoreHandler(ILogger<RegularModeScoreHandler> logger)
    {
        _logger = logger;
    }

    public void UpdateScore(Player player, bool isCorrect)
    {
        if (!isCorrect)
        {
            return;
        }

        player.Score += 1;
        _logger.LogInformation("Player {PlayerName} scored in regular mode! Score: {Score}",
            player.DisplayName, player.Score);
    }

    public (int threshold, int targetBuffer) GetQuestionThresholds()
    {
        return (threshold: 5, targetBuffer: 10);
    }
}
