using Microsoft.Extensions.Logging.Abstractions;
using StrongLink.Worker.Domain;
using StrongLink.Worker.Services;

namespace StrongLink.Worker.Tests.Services;

/// <summary>
/// Unit tests for SuddenDeathService.DetermineIfSuddenDeathNeeded.
/// Every test operates on the decision function in isolation — no game loop involved.
/// </summary>
public class SuddenDeathServiceTests
{
    private readonly SuddenDeathService _sut = new(NullLogger<SuddenDeathService>.Instance);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static GameSession MakeSession(params int[] scores)
    {
        var session = new GameSession
        {
            ChatId = 1,
            Topics = new[] { "T" },
            Tours = 3,
            RoundsPerTour = 10,
            AnswerTimeoutSeconds = 30,
            Status = GameStatus.InProgress
        };

        for (var i = 0; i < scores.Length; i++)
        {
            session.Players.Add(new Player
            {
                Id = 1000 + i,
                DisplayName = $"P{i}",
                Status = PlayerStatus.Active,
                Score = scores[i]
            });
        }

        return session;
    }

    // ── DetermineIfSuddenDeathNeeded ──────────────────────────────────────────

    [Fact]
    public void NoSuddenDeath_WhenSkipCheckIsTrue()
    {
        var session = MakeSession(3, 3, 3);
        var result = _sut.DetermineIfSuddenDeathNeeded(session, skipCheck: true);
        Assert.False(result.IsNeeded);
    }

    [Fact]
    public void NoSuddenDeath_WhenOnlyOneActivePlayer()
    {
        var session = MakeSession(5);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.False(result.IsNeeded);
    }

    [Fact]
    public void NoSuddenDeath_WhenAllPlayersHaveDistinctScores()
    {
        // 4 players with unique scores — bottom player gets cleanly eliminated
        var session = MakeSession(10, 8, 6, 4);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.False(result.IsNeeded);
    }

    [Fact]
    public void NoSuddenDeath_WhenTiedLowestCanBeSafelyEliminated()
    {
        // 5 players: 2 tied at lowest, eliminating both leaves 3 — safe
        var session = MakeSession(10, 8, 6, 4, 4);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.False(result.IsNeeded);
    }

    [Fact]
    public void NoSuddenDeath_WhenSingleClearWinnerAfterEliminatingAllTied()
    {
        // 3 players: top 1 has highest, bottom 2 are tied — eliminating tied pair leaves 1 winner
        var session = MakeSession(10, 4, 4);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.False(result.IsNeeded);
    }

    [Fact]
    public void SuddenDeath_WhenAllPlayersAreTied()
    {
        // All 3 players at the same score — no one can be eliminated without SD
        var session = MakeSession(5, 5, 5);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.True(result.IsNeeded);
        Assert.Equal(3, result.Participants.Count);
    }

    [Fact]
    public void SuddenDeath_ParticipantsAreOnlyTheTiedLowestPlayers()
    {
        // 4 players: top 2 are clear, bottom 2 are tied — SD among bottom 2 only
        var session = MakeSession(10, 8, 4, 4);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.True(result.IsNeeded);
        Assert.Equal(2, result.Participants.Count);
        Assert.All(result.Participants, p => Assert.Equal(4, p.Score));
    }

    [Fact]
    public void SuddenDeath_TwoPlayersAllTied_BothParticipate()
    {
        var session = MakeSession(7, 7);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.True(result.IsNeeded);
        Assert.Equal(2, result.Participants.Count);
    }

    [Fact]
    public void NoSuddenDeath_WhenExactlyOneTiedLowest_CleanElimination()
    {
        // 3 players, only one is lowest — clean cut, no SD needed
        var session = MakeSession(10, 7, 3);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.False(result.IsNeeded);
    }

    [Fact]
    public void NoSuddenDeath_HighScoreTieIrrelevant_OnlyLowestMatters()
    {
        // Top 2 are tied at the highest, bottom 1 is lowest — just eliminate bottom cleanly
        var session = MakeSession(10, 10, 3);
        var result = _sut.DetermineIfSuddenDeathNeeded(session);
        Assert.False(result.IsNeeded);
    }

    // ── EnterSuddenDeath / ExitSuddenDeath ────────────────────────────────────

    [Fact]
    public void EnterSuddenDeath_SetsStatusAndResetsParticipantScores()
    {
        var session = MakeSession(5, 5, 5);
        session.Players[0].SuddenDeathScore = 99; // pre-existing score should be reset
        var participants = session.Players.ToList();

        _sut.EnterSuddenDeath(session, participants);

        Assert.Equal(GameStatus.SuddenDeath, session.Status);
        Assert.All(session.Players, p => Assert.Equal(0, p.SuddenDeathScore));
        Assert.True(session.Metadata.ContainsKey("SuddenDeathParticipants"));
        Assert.True(session.Metadata.ContainsKey("SuddenDeathStartRound"));
    }

    [Fact]
    public void ExitSuddenDeath_ClearsMetadataAndRestoresInProgress()
    {
        var session = MakeSession(5, 5, 5);
        _sut.EnterSuddenDeath(session, session.Players.ToList());

        _sut.ExitSuddenDeath(session);

        Assert.Equal(GameStatus.InProgress, session.Status);
        Assert.False(session.Metadata.ContainsKey("SuddenDeathParticipants"));
        Assert.False(session.Metadata.ContainsKey("SuddenDeathStartRound"));
    }

    // ── CheckIfSuddenDeathResolved ────────────────────────────────────────────

    [Fact]
    public void SuddenDeathNotResolved_WhenAllParticipantsHaveSameScore()
    {
        var session = MakeSession(5, 5, 5);
        _sut.EnterSuddenDeath(session, session.Players.ToList());
        // No one has answered yet — all SuddenDeathScore = 0

        var resolution = _sut.CheckIfSuddenDeathResolved(session);

        Assert.False(resolution.IsResolved);
        Assert.Equal(3, resolution.Survivors.Count);
        Assert.Empty(resolution.ToEliminate);
    }

    [Fact]
    public void SuddenDeathResolved_WhenOneClearWinner()
    {
        var session = MakeSession(5, 5, 5);
        _sut.EnterSuddenDeath(session, session.Players.ToList());

        // Simulate: P0 answered correctly, P1 and P2 did not
        session.Players[0].SuddenDeathScore = 1;

        var resolution = _sut.CheckIfSuddenDeathResolved(session);

        Assert.True(resolution.IsResolved);
        var survivor = Assert.Single(resolution.Survivors);
        Assert.Equal(2, resolution.ToEliminate.Count);
        Assert.Equal(session.Players[0].Id, survivor.Id);
        Assert.All(resolution.ToEliminate, p => Assert.Equal(0, p.SuddenDeathScore));
    }

    [Fact]
    public void SuddenDeathResolved_EliminatesOnlyLowestNotEveryone()
    {
        var session = MakeSession(5, 5, 5, 5);
        _sut.EnterSuddenDeath(session, session.Players.ToList());

        session.Players[0].SuddenDeathScore = 1;
        session.Players[1].SuddenDeathScore = 1;
        session.Players[2].SuddenDeathScore = 0;
        session.Players[3].SuddenDeathScore = 0;

        var resolution = _sut.CheckIfSuddenDeathResolved(session);

        Assert.True(resolution.IsResolved);
        Assert.Equal(2, resolution.Survivors.Count);
        Assert.Equal(2, resolution.ToEliminate.Count);
        Assert.All(resolution.Survivors, p => Assert.Equal(1, p.SuddenDeathScore));
        Assert.All(resolution.ToEliminate, p => Assert.Equal(0, p.SuddenDeathScore));
    }

    [Fact]
    public void CheckResolution_ReturnsFalse_WhenNotInSuddenDeathStatus()
    {
        var session = MakeSession(5, 5);
        // Status is InProgress, not SuddenDeath
        var resolution = _sut.CheckIfSuddenDeathResolved(session);
        Assert.False(resolution.IsResolved);
    }

    [Fact]
    public void CheckResolution_WorksAfterJsonRoundtrip_ParticipantsDeserializedAsJsonElement()
    {
        // Regression test: Metadata["SuddenDeathParticipants"] becomes JsonElement after
        // JSON serialization/deserialization of the session, not List<long>.
        // The tie must still be resolvable after a save/load cycle.
        var session = MakeSession(5, 5);
        _sut.EnterSuddenDeath(session, session.Players.ToList());

        // Simulate what System.Text.Json does to List<long> stored as object in a dictionary:
        // serialize the metadata value and deserialize it back as JsonElement.
        var json = System.Text.Json.JsonSerializer.Serialize(session.Metadata["SuddenDeathParticipants"]);
        session.Metadata["SuddenDeathParticipants"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

        // Also simulate SuddenDeathStartRound deserialized as JsonElement
        var roundJson = System.Text.Json.JsonSerializer.Serialize(session.Metadata["SuddenDeathStartRound"]);
        session.Metadata["SuddenDeathStartRound"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(roundJson);

        // One player answers correctly — tie should now be resolvable
        session.Players[0].SuddenDeathScore = 1;

        var resolution = _sut.CheckIfSuddenDeathResolved(session);

        Assert.True(resolution.IsResolved);
        Assert.Equal(session.Players[0].Id, Assert.Single(resolution.Survivors).Id);
        Assert.Equal(session.Players[1].Id, Assert.Single(resolution.ToEliminate).Id);
    }
}
