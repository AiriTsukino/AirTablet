namespace RaffleManager.Models;

public enum WinnerChatChannel
{
    Say,
    Shout,
    Yell,
}

[Serializable]
public sealed class VenueProfileStore
{
    public List<VenueProfile> Profiles { get; set; } = new();
}

[Serializable]
public sealed class VenueProfile
{
    public string Name { get; set; } = "Default";
    public string VenueName { get; set; } = "Default";
    public string CustomLogoPath { get; set; } = string.Empty;
    public int TicketPrice { get; set; } = 100_000;
    public int BaseJackpot { get; set; } = 5_000_000;
    public int WinnerSplitPercent { get; set; } = 50;
    public bool BogoBonusTicketsCountTowardJackpot { get; set; }

    public bool AnnounceWinner { get; set; } = true;
    public WinnerChatChannel AnnouncementChannel { get; set; } = WinnerChatChannel.Yell;
    public string WinnerMessageTemplate { get; set; } = "Congratulations {winner}! You won {payout} from the {jackpot} raffle jackpot!";

    public bool TickSoundEnabled { get; set; } = true;
    public float TickVolume { get; set; } = 0.30f;

    // Per-profile main-window splitter ratio. The contestant side starts at one third.
    public float MainWindowLeftPanelRatio { get; set; } = 0.33f;

    public RaffleState Data { get; set; } = new();
}

[Serializable]
public sealed class RaffleState
{
    public List<RaffleEntry> Entries { get; set; } = new();

    // Undo snapshots for active raffle edits. Kept as History for backwards compatibility with older profile files.
    public List<RaffleEntry[]> History { get; set; } = new();

    // Completed raffle pull records shown on the History tab.
    public List<WinnerRecord> WinnerHistory { get; set; } = new();
}

[Serializable]
public sealed class RaffleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public int Tickets { get; set; } = 1;

    // Tickets that contributed to the jackpot. -1 means legacy profile data where all tickets were paid.
    public int JackpotTickets { get; set; } = -1;

    public int EffectiveJackpotTickets => JackpotTickets < 0 ? Tickets : Math.Clamp(JackpotTickets, 0, Tickets);
    public int FreeTickets => Math.Max(0, Tickets - EffectiveJackpotTickets);
    public bool HasFreeTickets => FreeTickets > 0;

    public string DisplayName => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";
}

[Serializable]
public sealed class WinnerRecord
{
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public int Tickets { get; set; }
    public int JackpotTickets { get; set; }
    public int TotalTickets { get; set; }
    public int TotalJackpotTickets { get; set; }
    public int TotalParticipants { get; set; }
    public int Jackpot { get; set; }
    public int Payout { get; set; }
    public int SplitPercent { get; set; }
    public DateTime PulledAt { get; set; } = DateTime.Now;

    public string DisplayName => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";
}
