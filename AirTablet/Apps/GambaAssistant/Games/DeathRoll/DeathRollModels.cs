using GambaAssistant.Models.Players;

namespace GambaAssistant.Games.DeathRoll;

public enum DeathRollTournamentStatus
{
    Setup,
    Running,
    Complete
}

public enum DeathRollMatchStatus
{
    Waiting,
    SeedingRolls,
    Playing,
    Complete,
    Bye
}

[Serializable]
public sealed class DeathRollPlayer
{
    public PlayerIdentity Identity { get; set; }
    public string DisplayName => Identity.ToString();
    public bool Eliminated { get; set; }
}

[Serializable]
public sealed class DeathRollMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int RoundIndex { get; set; }
    public int MatchIndex { get; set; }
    public DeathRollPlayer? PlayerA { get; set; }
    public DeathRollPlayer? PlayerB { get; set; }
    public DeathRollPlayer? Winner { get; set; }
    public DeathRollPlayer? Loser { get; set; }
    public DeathRollPlayer? CurrentTurn { get; set; }
    public DeathRollMatchStatus Status { get; set; } = DeathRollMatchStatus.Waiting;
    public int CurrentMax { get; set; } = 999;
    public int? SeedRollA { get; set; }
    public int? SeedRollB { get; set; }
    public bool FirstDeathRollTaken { get; set; }
    public DateTime OpeningRollsAcceptedAfterUtc { get; set; } = DateTime.MinValue;
    public List<string> History { get; set; } = new();
    public string Label => DeathRollTournamentLabels.GetMatchLabel(RoundIndex, MatchIndex, 0);

    public bool HasPlayer(string displayName)
        => IsPlayer(PlayerA, displayName) || IsPlayer(PlayerB, displayName);

    public DeathRollPlayer? GetPlayer(string displayName)
    {
        if (IsPlayer(PlayerA, displayName)) return PlayerA;
        if (IsPlayer(PlayerB, displayName)) return PlayerB;
        return null;
    }

    public DeathRollPlayer? OtherPlayer(DeathRollPlayer player)
    {
        if (PlayerA is not null && string.Equals(PlayerA.DisplayName, player.DisplayName, StringComparison.OrdinalIgnoreCase)) return PlayerB;
        if (PlayerB is not null && string.Equals(PlayerB.DisplayName, player.DisplayName, StringComparison.OrdinalIgnoreCase)) return PlayerA;
        return null;
    }

    private static bool IsPlayer(DeathRollPlayer? player, string displayName)
    {
        if (player is null || string.IsNullOrWhiteSpace(displayName)) return false;
        var target = Normalize(displayName);
        var playerName = Normalize(player.DisplayName);
        var shortName = Normalize(player.Identity.Name);
        return playerName == target || shortName == target || playerName.Contains(target) || target.Contains(shortName);
    }

    private static string Normalize(string value)
        => new string(value.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '@').ToArray()).Trim().ToLowerInvariant();
}

public static class DeathRollTournamentLabels
{
    public static string GetStageName(int roundIndex, int totalRounds, bool plural = false)
    {
        if (totalRounds <= 0)
            return $"Round {roundIndex + 1}";

        var roundsRemaining = totalRounds - roundIndex;
        return roundsRemaining switch
        {
            <= 1 => plural ? "Final" : "Final",
            2 => plural ? "Semi-finals" : "Semi-final",
            3 => plural ? "Quarter-finals" : "Quarter-final",
            _ => $"Round of {(int)Math.Pow(2, roundsRemaining)}",
        };
    }

    public static string GetMatchLabel(int roundIndex, int matchIndex, int totalRounds)
    {
        var stage = GetStageName(roundIndex, totalRounds);
        return stage == "Final" ? "Final Match" : $"{stage} Match {matchIndex + 1}";
    }
}

[Serializable]
public sealed class DeathRollTournament
{
    public DeathRollTournamentStatus Status { get; set; } = DeathRollTournamentStatus.Setup;
    public List<DeathRollPlayer> Entrants { get; set; } = new();
    public List<List<DeathRollMatch>> Rounds { get; set; } = new();
    public Guid? ActiveMatchId { get; set; }
    public int StartingMax { get; set; } = 999;
    public int SeedingMax { get; set; } = 10;

    public IEnumerable<DeathRollMatch> AllMatches => Rounds.SelectMany(r => r);
    public DeathRollMatch? ActiveMatch => ActiveMatchId.HasValue ? AllMatches.FirstOrDefault(m => m.Id == ActiveMatchId.Value) : null;

    public void NormalizeForSave()
    {
        Entrants ??= new();
        Rounds ??= new();

        for (var roundIndex = 0; roundIndex < Rounds.Count; roundIndex++)
        {
            var round = Rounds[roundIndex] ?? new List<DeathRollMatch>();
            Rounds[roundIndex] = round;

            for (var matchIndex = 0; matchIndex < round.Count; matchIndex++)
                NormalizeMatch(round[matchIndex], roundIndex, matchIndex);
        }

        RelinkMatchPlayers();
        EnsureActiveMatchIsValid();
    }

    public void NormalizeAfterLoad() => NormalizeForSave();

    private void NormalizeMatch(DeathRollMatch? match, int roundIndex, int matchIndex)
    {
        if (match is null)
            return;

        if (match.Id == Guid.Empty)
            match.Id = Guid.NewGuid();

        match.RoundIndex = roundIndex;
        match.MatchIndex = matchIndex;
        match.CurrentMax = match.CurrentMax <= 0 ? StartingMax : match.CurrentMax;
        match.History ??= new();
    }

    private void RelinkMatchPlayers()
    {
        for (var i = 0; i < Entrants.Count; i++)
            Entrants[i] = CanonicalizePlayer(Entrants[i]) ?? Entrants[i];

        foreach (var match in AllMatches)
        {
            match.PlayerA = CanonicalizePlayer(match.PlayerA);
            match.PlayerB = CanonicalizePlayer(match.PlayerB);
            match.Winner = CanonicalizePlayer(match.Winner);
            match.Loser = CanonicalizePlayer(match.Loser);
            match.CurrentTurn = CanonicalizePlayer(match.CurrentTurn);
        }
    }

    private DeathRollPlayer? CanonicalizePlayer(DeathRollPlayer? player)
    {
        if (player is null)
            return null;

        var existing = Entrants.FirstOrDefault(e => SameIdentity(e.Identity, player.Identity));
        if (existing is null)
        {
            Entrants.Add(player);
            existing = player;
        }

        existing.Eliminated |= player.Eliminated;
        return existing;
    }

    private void EnsureActiveMatchIsValid()
    {
        if (Status == DeathRollTournamentStatus.Setup)
        {
            ActiveMatchId = null;
            return;
        }

        if (ActiveMatch is not null || Status == DeathRollTournamentStatus.Complete)
            return;

        ActiveMatchId = AllMatches.FirstOrDefault(m =>
            m.PlayerA is not null &&
            m.PlayerB is not null &&
            m.Status is DeathRollMatchStatus.Waiting or DeathRollMatchStatus.SeedingRolls or DeathRollMatchStatus.Playing)?.Id;
    }

    private static bool SameIdentity(PlayerIdentity a, PlayerIdentity b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(a.World)
            || string.IsNullOrWhiteSpace(b.World)
            || string.Equals(a.World, b.World, StringComparison.OrdinalIgnoreCase);
    }
}
