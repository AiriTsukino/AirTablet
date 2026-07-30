using System.Security.Cryptography;
using Dalamud.Game.ClientState.Objects.SubKinds;
using RaffleManager.Models;

namespace RaffleManager.Services;

internal sealed class RaffleService
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;

    public RaffleService(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
    }

    private VenueProfile Profile => config.Profile;
    private RaffleState Data => Profile.Data;

    public IReadOnlyList<RaffleEntry> Entries => Data.Entries;
    public IReadOnlyList<WinnerRecord> WinnerHistory => Data.WinnerHistory;
    public int ParticipantCount => Data.Entries.Count;
    public int TotalTickets => Data.Entries.Sum(e => Math.Max(0, e.Tickets));
    public int TotalJackpotTickets => Data.Entries.Sum(e => Math.Max(0, e.EffectiveJackpotTickets));
    public int Jackpot => (int)Math.Min(int.MaxValue, (long)Profile.BaseJackpot + ((long)TotalJackpotTickets * Profile.TicketPrice));
    public int WinnerPayout => (int)Math.Min(int.MaxValue, Math.Floor(Jackpot * (Profile.WinnerSplitPercent / 100.0)));

    public string LastStatus { get; private set; } = "Ready.";

    public bool AddOrUpdate(string name, string world, int tickets, bool countTowardJackpot = true)
        => AddOrUpdate(name, world, tickets, countTowardJackpot ? tickets : 0);

    public bool AddOrUpdate(string name, string world, int tickets, int jackpotTickets)
    {
        name = (name ?? string.Empty).Trim();
        world = (world ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            LastStatus = "Enter a character name first.";
            return false;
        }

        if (tickets < 1)
        {
            LastStatus = "Tickets must be at least 1.";
            return false;
        }

        jackpotTickets = Math.Clamp(jackpotTickets, 0, tickets);

        Snapshot();
        var existing = Data.Entries.FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            e.World.Equals(world, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var currentJackpotTickets = existing.EffectiveJackpotTickets;
            existing.Tickets += tickets;
            existing.JackpotTickets = currentJackpotTickets + jackpotTickets;
            var freeTickets = tickets - jackpotTickets;
            var freeNote = freeTickets > 0 ? $" ({freeTickets:N0} free/VIP)" : string.Empty;
            LastStatus = $"Updated {existing.DisplayName}: now {existing.Tickets:N0} ticket(s){freeNote}.";
        }
        else
        {
            Data.Entries.Add(new RaffleEntry
            {
                Name = name,
                World = world,
                Tickets = tickets,
                JackpotTickets = jackpotTickets,
            });
            var freeTickets = tickets - jackpotTickets;
            var freeNote = freeTickets > 0 ? $" ({freeTickets:N0} free/VIP)" : string.Empty;
            LastStatus = $"Added {name}{(string.IsNullOrWhiteSpace(world) ? string.Empty : $"@{world}")} with {tickets:N0} ticket(s){freeNote}.";
        }

        persistence.SaveNow();
        return true;
    }

    public bool Remove(Guid id)
    {
        var entry = Data.Entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return false;
        Snapshot();
        Data.Entries.Remove(entry);
        LastStatus = $"Removed {entry.DisplayName}.";
        persistence.SaveNow();
        return true;
    }

    public bool Undo()
    {
        if (Data.History.Count == 0)
        {
            LastStatus = "No previous raffle state to restore.";
            return false;
        }

        var previous = Data.History[^1];
        Data.History.RemoveAt(Data.History.Count - 1);
        Data.Entries = previous.Select(Clone).ToList();
        LastStatus = "Restored previous raffle state.";
        persistence.SaveNow();
        return true;
    }

    public void Clear()
    {
        if (Data.Entries.Count == 0) return;
        Snapshot();
        Data.Entries.Clear();
        LastStatus = "Cleared all contestants.";
        persistence.SaveNow();
    }

    public RaffleEntry? PickRandomTicketOwner()
    {
        var total = TotalTickets;
        if (total <= 0) return null;

        var roll = RandomNumberGenerator.GetInt32(total);
        var cumulative = 0;
        foreach (var entry in Data.Entries.Where(e => e.Tickets > 0))
        {
            cumulative += entry.Tickets;
            if (roll < cumulative) return entry;
        }

        return Data.Entries.LastOrDefault(e => e.Tickets > 0);
    }

    public WinnerRecord? PullWinner()
    {
        var entry = PickRandomTicketOwner();
        if (entry is null)
        {
            LastStatus = "Add contestants before pulling a winner.";
            return null;
        }

        var winner = new WinnerRecord
        {
            Name = entry.Name,
            World = entry.World,
            Tickets = entry.Tickets,
            JackpotTickets = entry.EffectiveJackpotTickets,
            TotalTickets = TotalTickets,
            TotalJackpotTickets = TotalJackpotTickets,
            TotalParticipants = ParticipantCount,
            Jackpot = Jackpot,
            Payout = WinnerPayout,
            SplitPercent = Profile.WinnerSplitPercent,
            PulledAt = DateTime.Now,
        };
        Data.WinnerHistory.Add(winner);
        if (Data.WinnerHistory.Count > 500)
            Data.WinnerHistory.RemoveRange(0, Data.WinnerHistory.Count - 500);

        LastStatus = $"Winner pulled: {winner.DisplayName} for {winner.Payout:N0} gil.";
        persistence.SaveNow();
        return winner;
    }

    public void ClearWinnerHistory()
    {
        if (Data.WinnerHistory.Count == 0) return;
        Data.WinnerHistory.Clear();
        LastStatus = "Winner history cleared.";
        persistence.SaveNow();
    }

    public bool AddCurrentTarget(int tickets, bool countTowardJackpot = true)
        => AddCurrentTarget(tickets, countTowardJackpot ? tickets : 0);

    public bool AddCurrentTarget(int tickets, int jackpotTickets)
    {
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter pc)
        {
            LastStatus = "Target a player first, then click Add Target.";
            return false;
        }

        var name = pc.Name.ToString();
        var world = string.Empty;
        try { world = pc.HomeWorld.Value.Name.ToString(); }
        catch { world = string.Empty; }
        return AddOrUpdate(name, world, tickets, jackpotTickets);
    }

    private void Snapshot()
    {
        Data.History.Add(Data.Entries.Select(Clone).ToArray());
        if (Data.History.Count > 50)
            Data.History.RemoveAt(0);
    }

    private static RaffleEntry Clone(RaffleEntry entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        World = entry.World,
        Tickets = entry.Tickets,
        JackpotTickets = entry.EffectiveJackpotTickets,
    };
}
