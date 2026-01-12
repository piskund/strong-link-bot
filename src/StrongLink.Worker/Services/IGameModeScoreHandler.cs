using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

/// <summary>
/// Handles scoring for a specific game mode (regular or sudden death).
/// Each implementation is responsible for updating the appropriate score fields.
/// </summary>
public interface IGameModeScoreHandler
{
    /// <summary>
    /// Updates the player's score for this game mode.
    /// Regular mode updates Score, sudden death mode updates SuddenDeathScore.
    /// </summary>
    /// <param name="player">The player whose score should be updated</param>
    /// <param name="isCorrect">Whether the answer was correct</param>
    void UpdateScore(Player player, bool isCorrect);

    /// <summary>
    /// Gets the question threshold and target buffer for this game mode.
    /// Sudden death requires more questions in reserve.
    /// </summary>
    /// <returns>A tuple of (threshold, targetBuffer) values</returns>
    (int threshold, int targetBuffer) GetQuestionThresholds();
}
