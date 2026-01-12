using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

/// <summary>
/// Service responsible for managing sudden death logic in game sessions.
/// </summary>
public interface ISuddenDeathService
{
    /// <summary>
    /// Determines if sudden death is needed at tour completion based on player scores and ties.
    /// </summary>
    /// <param name="session">The current game session</param>
    /// <param name="skipCheck">Whether to skip the sudden death check (e.g., after resolution)</param>
    /// <returns>A decision indicating if sudden death is needed and which players should participate</returns>
    SuddenDeathDecision DetermineIfSuddenDeathNeeded(GameSession session, bool skipCheck = false);

    /// <summary>
    /// Checks if sudden death is resolved after a round by examining participant scores.
    /// </summary>
    /// <param name="session">The current game session</param>
    /// <returns>A resolution indicating if ties are broken and which players should be eliminated</returns>
    SuddenDeathResolution CheckIfSuddenDeathResolved(GameSession session);

    /// <summary>
    /// Enters sudden death mode by setting up the session and participants.
    /// </summary>
    /// <param name="session">The current game session</param>
    /// <param name="participants">The players who will participate in sudden death</param>
    void EnterSuddenDeath(GameSession session, List<Player> participants);

    /// <summary>
    /// Exits sudden death mode by clearing sudden death state from the session.
    /// </summary>
    /// <param name="session">The current game session</param>
    void ExitSuddenDeath(GameSession session);
}

/// <summary>
/// Represents a decision about whether sudden death is needed.
/// </summary>
public record SuddenDeathDecision
{
    /// <summary>
    /// Whether sudden death is needed.
    /// </summary>
    public bool IsNeeded { get; init; }

    /// <summary>
    /// The players who should participate in sudden death.
    /// </summary>
    public List<Player> Participants { get; init; } = new();

    /// <summary>
    /// The reason for the decision (for logging purposes).
    /// </summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Represents the resolution status of a sudden death round.
/// </summary>
public record SuddenDeathResolution
{
    /// <summary>
    /// Whether the sudden death ties have been resolved.
    /// </summary>
    public bool IsResolved { get; init; }

    /// <summary>
    /// Players who should be eliminated (those with the lowest sudden death score).
    /// </summary>
    public List<Player> ToEliminate { get; init; } = new();

    /// <summary>
    /// Players who survived the sudden death round.
    /// </summary>
    public List<Player> Survivors { get; init; } = new();
}
