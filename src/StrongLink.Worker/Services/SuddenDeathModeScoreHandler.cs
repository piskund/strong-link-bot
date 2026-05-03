using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

/// <summary>
/// Handles scoring for sudden death mode.
/// Updates the SuddenDeathScore field and uses higher question thresholds
/// since we don't know how long sudden death will last.
/// </summary>
public sealed class SuddenDeathModeScoreHandler : IGameModeScoreHandler
{
    private readonly ILogger<SuddenDeathModeScoreHandler> _logger;

    public SuddenDeathModeScoreHandler(ILogger<SuddenDeathModeScoreHandler> logger)
    {
        _logger = logger;
    }

    public void UpdateScore(Player player, bool isCorrect)
    {
        if (!isCorrect)
        {
            return;
        }

        player.SuddenDeathScore += 1;
        _logger.LogInformation("Player {PlayerName} scored in sudden death! SuddenDeathScore: {SuddenDeathScore}",
            player.DisplayName, player.SuddenDeathScore);
    }

    public (int threshold, int targetBuffer) GetQuestionThresholds()
    {
        // SD uses 1 question per participant per round — a small buffer is fine.
        // Keeping this low avoids triggering background generation during SD, which
        // would race with the foreground saving SD metadata.
        return (threshold: 5, targetBuffer: 10);
    }
}
