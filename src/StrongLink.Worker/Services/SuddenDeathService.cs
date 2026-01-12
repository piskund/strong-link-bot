using StrongLink.Worker.Domain;

namespace StrongLink.Worker.Services;

public sealed class SuddenDeathService : ISuddenDeathService
{
    private readonly ILogger<SuddenDeathService> _logger;

    public SuddenDeathService(ILogger<SuddenDeathService> logger)
    {
        _logger = logger;
    }

    public SuddenDeathDecision DetermineIfSuddenDeathNeeded(GameSession session, bool skipCheck = false)
    {
        if (skipCheck)
        {
            _logger.LogInformation("Skipping sudden death check as requested");
            return new SuddenDeathDecision { IsNeeded = false, Reason = "Check skipped" };
        }

        var activePlayers = session.ActivePlayers.ToList();
        if (activePlayers.Count <= 1)
        {
            _logger.LogDebug("No sudden death needed: {Count} active player(s)", activePlayers.Count);
            return new SuddenDeathDecision { IsNeeded = false, Reason = "Not enough players" };
        }

        // Check if we can safely eliminate players tied for lowest score
        if (activePlayers.Count > 1)
        {
            var minScore = activePlayers.Min(p => p.Score);
            var tiedForLowest = activePlayers
                .Where(p => p.Score == minScore)
                .ToList();

            var remainingAfterElimination = activePlayers.Count - tiedForLowest.Count;

            _logger.LogInformation("Tied for lowest score ({MinScore}): {Count} player(s). Would leave: {Remaining}",
                minScore, tiedForLowest.Count, remainingAfterElimination);

            if (remainingAfterElimination >= 3)
            {
                // Safe to eliminate - no sudden death needed
                _logger.LogDebug("Safe to eliminate {Count} player(s) tied for lowest score without sudden death",
                    tiedForLowest.Count);
                return new SuddenDeathDecision
                {
                    IsNeeded = false,
                    Reason = $"Can safely eliminate {tiedForLowest.Count} player(s)"
                };
            }

            if (remainingAfterElimination >= 1)
            {
                // Would leave 1-2 players, need to check if sudden death is needed
                _logger.LogInformation("Elimination would leave {Remaining} players. Checking for sudden death need.",
                    remainingAfterElimination);

                if (tiedForLowest.Count > 1)
                {
                    // Multiple players tied for lowest - need sudden death to determine final rankings
                    _logger.LogInformation("Sudden death needed: {Count} players tied for lowest score",
                        tiedForLowest.Count);

                    return new SuddenDeathDecision
                    {
                        IsNeeded = true,
                        Participants = tiedForLowest,
                        Reason = $"{tiedForLowest.Count} players tied for lowest score"
                    };
                }
                else
                {
                    // Only one player with lowest score - no sudden death needed
                    _logger.LogDebug("Only one player with lowest score - no sudden death needed");
                    return new SuddenDeathDecision
                    {
                        IsNeeded = false,
                        Reason = "Single player with lowest score"
                    };
                }
            }
        }

        // Check if we have 3 or fewer active players with ties
        activePlayers = session.ActivePlayers.ToList();
        if (activePlayers.Count <= 3 && activePlayers.Count > 1)
        {
            var tiedGroups = activePlayers.GroupBy(p => p.Score).Where(g => g.Count() > 1).ToList();
            if (tiedGroups.Any())
            {
                // Have ties among final 3 or fewer - need sudden death
                var tiedPlayers = tiedGroups.SelectMany(g => g).ToList();
                _logger.LogInformation("Final {Count} players have ties. Sudden death needed for {TiedCount} tied players.",
                    activePlayers.Count, tiedPlayers.Count);

                return new SuddenDeathDecision
                {
                    IsNeeded = true,
                    Participants = tiedPlayers,
                    Reason = $"Ties among final {activePlayers.Count} players"
                };
            }
        }

        return new SuddenDeathDecision { IsNeeded = false, Reason = "No ties to resolve" };
    }

    public SuddenDeathResolution CheckIfSuddenDeathResolved(GameSession session)
    {
        if (session.Status != GameStatus.SuddenDeath)
        {
            _logger.LogWarning("CheckIfSuddenDeathResolved called but session is not in sudden death mode");
            return new SuddenDeathResolution { IsResolved = false };
        }

        if (!session.Metadata.TryGetValue("SuddenDeathParticipants", out var participantsObj) ||
            participantsObj is not List<long> participantIds)
        {
            _logger.LogWarning("No sudden death participants found in metadata");
            return new SuddenDeathResolution { IsResolved = false };
        }

        var participants = participantIds
            .Select(id => session.FindPlayer(id))
            .Where(p => p != null && p.Status == PlayerStatus.Active)
            .Cast<Player>()
            .ToList();

        _logger.LogDebug("Checking sudden death progress after round. Participants: {Count}", participants.Count);

        // Check if there's a clear separation in scores
        var suddenDeathScores = participants.Select(p => p.SuddenDeathScore).ToList();
        var minScore = suddenDeathScores.Min();
        var maxScore = suddenDeathScores.Max();

        if (maxScore > minScore)
        {
            _logger.LogInformation("Sudden death resolved. Ties broken. Min: {Min}, Max: {Max}. Eliminating lowest scorers.",
                minScore, maxScore);

            // Eliminate all players with the lowest score
            var toEliminate = participants.Where(p => p.SuddenDeathScore == minScore).ToList();
            var survivors = participants.Where(p => p.SuddenDeathScore > minScore).ToList();

            return new SuddenDeathResolution
            {
                IsResolved = true,
                ToEliminate = toEliminate,
                Survivors = survivors
            };
        }
        else
        {
            _logger.LogInformation("Ties still present in sudden death. Continuing.");
            return new SuddenDeathResolution
            {
                IsResolved = false,
                ToEliminate = new List<Player>(),
                Survivors = participants
            };
        }
    }

    public void EnterSuddenDeath(GameSession session, List<Player> participants)
    {
        _logger.LogInformation("Entering sudden death mode for {Count} participants", participants.Count);

        // Reset sudden death scores for participants
        foreach (var player in participants)
        {
            player.SuddenDeathScore = 0;
        }

        session.Status = GameStatus.SuddenDeath;

        // Track which players are in sudden death
        session.Metadata["SuddenDeathParticipants"] = participants.Select(p => p.Id).ToList();

        // Initialize the sudden death start round tracker
        // This will be used to enforce the round limit
        session.Metadata["SuddenDeathStartRound"] = session.CurrentRound;

        _logger.LogInformation("Sudden death initialized with participants: {Players}, starting at round {Round}",
            string.Join(", ", participants.Select(p => p.DisplayName)), session.CurrentRound);
    }

    public void ExitSuddenDeath(GameSession session)
    {
        _logger.LogInformation("Exiting sudden death mode");

        // Clear sudden death state for ALL players (including survivors)
        foreach (var player in session.Players)
        {
            player.SuddenDeathScore = 0;
        }

        session.Metadata.Remove("SuddenDeathParticipants");
        session.Metadata.Remove("SuddenDeathStartRound");
        session.Status = GameStatus.InProgress;
        session.TurnQueue.Clear();

        _logger.LogInformation("Sudden death state cleared");
    }
}
