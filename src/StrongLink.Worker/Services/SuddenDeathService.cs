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

        var minScore = activePlayers.Min(p => p.Score);
        var tiedForLowest = activePlayers.Where(p => p.Score == minScore).ToList();
        var remainingAfterElimination = activePlayers.Count - tiedForLowest.Count;

        _logger.LogInformation("End-of-tour: {Total} active players, tied for lowest ({MinScore}): {TiedCount}, would leave: {Remaining}",
            activePlayers.Count, minScore, tiedForLowest.Count, remainingAfterElimination);

        // All players tied — no one can be eliminated without sudden death
        if (remainingAfterElimination == 0)
        {
            _logger.LogInformation("All {Count} players tied — sudden death needed", activePlayers.Count);
            return new SuddenDeathDecision
            {
                IsNeeded = true,
                Participants = tiedForLowest,
                Reason = $"All {activePlayers.Count} players tied"
            };
        }

        // Exactly one player has a higher score — clear winner/survivor, eliminate the rest
        if (remainingAfterElimination == 1)
        {
            _logger.LogInformation("One player above minimum score. Eliminating {Count} tied-for-lowest. No sudden death needed.", tiedForLowest.Count);
            return new SuddenDeathDecision { IsNeeded = false, Reason = "Single player above minimum" };
        }

        // Would leave ≥2 players after elimination:
        // If only one player has the lowest score, eliminate them cleanly.
        // If multiple share the lowest, we need sudden death to decide which one to drop
        // — but only if we'd otherwise drop below the viable player count (≥2 remaining).
        if (tiedForLowest.Count == 1)
        {
            _logger.LogDebug("Single player with lowest score — clean elimination, no sudden death needed");
            return new SuddenDeathDecision { IsNeeded = false, Reason = "Single player with lowest score" };
        }

        // Multiple players tied for lowest. If safe to eliminate all of them (≥3 would remain), do so.
        if (remainingAfterElimination >= 3)
        {
            _logger.LogDebug("Safe to eliminate all {Count} tied-for-lowest players ({Remaining} remain)", tiedForLowest.Count, remainingAfterElimination);
            return new SuddenDeathDecision { IsNeeded = false, Reason = $"Safe to eliminate {tiedForLowest.Count} tied players" };
        }

        // remainingAfterElimination == 2 and tiedForLowest.Count > 1:
        // Eliminating all tied-lowest players would leave exactly 2, which is fine for continuing.
        // However we still need to pick which of the tied-lowest players actually leaves —
        // use sudden death only among those tied for last place.
        _logger.LogInformation("Sudden death needed: {Count} players tied for lowest score, eliminating all would leave {Remaining}",
            tiedForLowest.Count, remainingAfterElimination);
        return new SuddenDeathDecision
        {
            IsNeeded = true,
            Participants = tiedForLowest,
            Reason = $"{tiedForLowest.Count} players tied for lowest score"
        };
    }

    public SuddenDeathResolution CheckIfSuddenDeathResolved(GameSession session)
    {
        if (session.Status != GameStatus.SuddenDeath)
        {
            _logger.LogWarning("CheckIfSuddenDeathResolved called but session is not in sudden death mode");
            return new SuddenDeathResolution { IsResolved = false };
        }

        if (!session.Metadata.TryGetValue("SuddenDeathParticipants", out var participantsObj))
        {
            _logger.LogWarning("No sudden death participants found in metadata");
            return new SuddenDeathResolution { IsResolved = false };
        }

        List<long> participantIds;
        if (participantsObj is List<long> directList)
        {
            participantIds = directList;
        }
        else if (participantsObj is System.Text.Json.JsonElement jsonElement &&
                 jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            participantIds = new List<long>();
            foreach (var item in jsonElement.EnumerateArray())
                participantIds.Add(item.GetInt64());
            // Write back the deserialized list so future calls don't re-parse
            session.Metadata["SuddenDeathParticipants"] = participantIds;
        }
        else
        {
            _logger.LogWarning("SuddenDeathParticipants has unexpected type {Type}", participantsObj?.GetType().Name);
            return new SuddenDeathResolution { IsResolved = false };
        }

        var participants = participantIds
            .Select(id => session.FindPlayer(id))
            .Where(p => p != null && p.Status == PlayerStatus.Active)
            .Cast<Player>()
            .ToList();

        _logger.LogDebug("Checking sudden death progress after round. Participants: {Count}", participants.Count);

        if (participants.Count == 0)
        {
            _logger.LogError("No participants found in sudden death check - this should not happen!");
            return new SuddenDeathResolution { IsResolved = false };
        }

        // Check if there's a clear separation in scores
        var suddenDeathScores = participants.Select(p => p.SuddenDeathScore).ToList();
        var minScore = suddenDeathScores.Min();
        var maxScore = suddenDeathScores.Max();

        _logger.LogInformation("Sudden death score check: Min={Min}, Max={Max}, Participants={Participants}",
            minScore, maxScore, string.Join(", ", participants.Select(p => $"{p.DisplayName}:{p.SuddenDeathScore}")));

        if (maxScore > minScore)
        {
            _logger.LogInformation("Sudden death resolved. Ties broken. Min: {Min}, Max: {Max}. Eliminating lowest scorers.",
                minScore, maxScore);

            // Eliminate all players with the lowest score
            var toEliminate = participants.Where(p => p.SuddenDeathScore == minScore).ToList();
            var survivors = participants.Where(p => p.SuddenDeathScore > minScore).ToList();

            _logger.LogInformation("To eliminate: {ToElim}, Survivors: {Survivors}",
                string.Join(", ", toEliminate.Select(p => p.DisplayName)),
                string.Join(", ", survivors.Select(p => p.DisplayName)));

            return new SuddenDeathResolution
            {
                IsResolved = true,
                ToEliminate = toEliminate,
                Survivors = survivors
            };
        }
        else
        {
            _logger.LogInformation("Ties still present in sudden death (all scores = {Score}). Continuing.", minScore);
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
        _logger.LogInformation("Entering sudden death mode for {Count} participants: {Players}",
            participants.Count,
            string.Join(", ", participants.Select(p => $"{p.DisplayName}(Score:{p.Score})")));

        // Reset sudden death scores for participants
        foreach (var player in participants)
        {
            player.SuddenDeathScore = 0;
            _logger.LogDebug("Reset sudden death score for {Player}", player.DisplayName);
        }

        session.Status = GameStatus.SuddenDeath;

        // Track which players are in sudden death
        var participantIds = participants.Select(p => p.Id).ToList();
        session.Metadata["SuddenDeathParticipants"] = participantIds;

        // Initialize the sudden death start round tracker
        // This will be used to enforce the round limit
        session.Metadata["SuddenDeathStartRound"] = session.CurrentRound;

        _logger.LogInformation("Sudden death initialized. Participant IDs: [{Ids}], Starting round: {Round}",
            string.Join(", ", participantIds), session.CurrentRound);
    }

    public void ExitSuddenDeath(GameSession session)
    {
        _logger.LogInformation("Exiting sudden death mode");

        // DO NOT reset SuddenDeathScore - we need these scores to determine the final winner!
        // Only clear the metadata and status tracking
        session.Metadata.Remove("SuddenDeathParticipants");
        session.Metadata.Remove("SuddenDeathStartRound");
        session.Status = GameStatus.InProgress;
        session.TurnQueue.Clear();

        _logger.LogInformation("Sudden death state cleared. Scores preserved for winner determination: {Scores}",
            string.Join(", ", session.Players.Select(p => $"{p.DisplayName}:{p.SuddenDeathScore}")));
    }
}
