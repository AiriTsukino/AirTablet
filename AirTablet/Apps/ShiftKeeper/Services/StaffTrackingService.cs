using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using ShiftKeeper.Models;

namespace ShiftKeeper.Services;

public sealed class StaffTrackingService : IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly Dictionary<Guid, DateTimeOffset> pendingRecoveryByVenue = [];
    private readonly Dictionary<Guid, HashSet<Guid>> pendingRecoveryStaffByVenue = [];
    private readonly Dictionary<Guid, int> recoveryVisibleScanCounts = [];
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow;
    private DateTimeOffset lastScan = DateTimeOffset.MinValue;
    private bool disposed;

    public StaffTrackingService(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
        var activeVenue = persistence.ActiveVenue;
        if (activeVenue.CurrentNight.EndedUtc is null && activeVenue.CurrentNight.TrackingCheckpointUtc > DateTimeOffset.MinValue)
        {
            var candidates = GetRecoveryCandidates(activeVenue);
            if (candidates.Count > 0)
            {
                pendingRecoveryByVenue[activeVenue.Id] = activeVenue.CurrentNight.TrackingCheckpointUtc;
                pendingRecoveryStaffByVenue[activeVenue.Id] = candidates;
            }
        }
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed || DateTimeOffset.UtcNow - lastScan < TimeSpan.FromSeconds(1)) return;
        var now = DateTimeOffset.UtcNow;
        var elapsed = Math.Clamp((now - lastTick).TotalSeconds, 0, 5);
        lastTick = now;
        lastScan = now;
        Tick(now, elapsed);
    }

    private void Tick(DateTimeOffset now, double elapsed)
    {
        var venue = persistence.ActiveVenue;
        var visible = CollectVisiblePlayers();
        var localNow = DateTime.Now;
        var recovery = TryApplyCrashRecovery(venue, visible, now);
        if (recovery == RecoveryResult.Applied)
            elapsed = 0;

        pendingRecoveryStaffByVenue.TryGetValue(venue.Id, out var recoveryCandidates);

        foreach (var member in venue.Staff.Where(x => x.Enabled))
        {
            var record = venue.CurrentNight.GetOrCreate(member.Id);
            var isVisible = visible.Contains(Key(member.Name, member.World));
            var inSchedule = config.TrackOnlyDuringScheduledShift
                ? venue.IsInAssignedShift(member, localNow)
                : venue.IsTrackingTime(localNow);
            var shouldAccrue = false;

            record.IsVisible = isVisible;
            record.InCrashGrace = false;
            if (recovery == RecoveryResult.WaitingForVenueReturn && recoveryCandidates?.Contains(member.Id) == true)
            {
                ReconcileAutomaticPaidStatus(member, record, venue, now);
                continue;
            }
            if (!record.Scheduled || record.ShiftEndedEarly)
            {
                ReconcileAutomaticPaidStatus(member, record, venue, now);
                continue;
            }

            switch (member.PresenceMode)
            {
                case PresenceMode.VenueTracking:
                    if (isVisible)
                    {
                        if (inSchedule)
                        {
                            record.LastSeenUtc = now;
                            record.HasWorked = true;
                            shouldAccrue = true;
                        }
                    }
                    else if (inSchedule && record.LastSeenUtc is { } lastSeen && now - lastSeen <= TimeSpan.FromMinutes(config.CrashGraceMinutes))
                    {
                        record.InCrashGrace = true;
                        shouldAccrue = true;
                    }
                    break;
                case PresenceMode.ManualTimer:
                    if (record.ManualClockedIn && inSchedule)
                    {
                        record.HasWorked = true;
                        shouldAccrue = true;
                    }
                    break;
                case PresenceMode.NoTimer:
                    break;
            }

            if (shouldAccrue && elapsed > 0)
                record.AccruedSeconds += elapsed;

            ReconcileAutomaticPaidStatus(member, record, venue, now);
        }

        venue.CurrentNight.TrackingCheckpointUtc = now;
        persistence.MarkDirty();
        persistence.SaveIfDue();
    }

    private void ReconcileAutomaticPaidStatus(StaffMember member, NightlyStaffRecord record, VenueProfile venue, DateTimeOffset now)
    {
        var remaining = PayCalculator.CalculateRemainingDue(member, record, venue, config);
        var fullyPaidAfterWork = record.TotalPaidGil > 0 && remaining == 0 && PayCalculator.IsWorkComplete(member, record, venue);

        if (!record.Paid && fullyPaidAfterWork)
        {
            record.Paid = true;
            record.PaidAutomatically = true;
            record.PaidUtc = now;
        }
        else if (record.Paid && record.PaidAutomatically && !fullyPaidAfterWork)
        {
            record.Paid = false;
            record.PaidAutomatically = false;
            record.PaidUtc = null;
        }
    }

    private RecoveryResult TryApplyCrashRecovery(VenueProfile venue, HashSet<string> visible, DateTimeOffset now)
    {
        if (!pendingRecoveryByVenue.TryGetValue(venue.Id, out var checkpoint))
            return RecoveryResult.None;
        if (!pendingRecoveryStaffByVenue.TryGetValue(venue.Id, out var recoveryCandidates))
        {
            pendingRecoveryByVenue.Remove(venue.Id);
            return RecoveryResult.Skipped;
        }

        if (!config.HostCrashRecoveryEnabled)
        {
            pendingRecoveryByVenue.Remove(venue.Id);
            pendingRecoveryStaffByVenue.Remove(venue.Id);
            recoveryVisibleScanCounts.Remove(venue.Id);
            return RecoveryResult.Skipped;
        }

        if (!DalamudServices.ClientState.IsLoggedIn || DalamudServices.ClientState.TerritoryType == 0 || DalamudServices.ObjectTable.LocalPlayer is null)
            return RecoveryResult.WaitingForVenueReturn;

        recoveryCandidates.IntersectWith(venue.Staff
            .Where(x => x.Enabled && x.PresenceMode != PresenceMode.NoTimer)
            .Select(x => x.Id));
        if (recoveryCandidates.Count == 0)
        {
            pendingRecoveryByVenue.Remove(venue.Id);
            pendingRecoveryStaffByVenue.Remove(venue.Id);
            recoveryVisibleScanCounts.Remove(venue.Id);
            return RecoveryResult.Skipped;
        }

        var readyStaff = venue.Staff
            .Where(member =>
            {
                if (!recoveryCandidates.Contains(member.Id)) return false;
                var record = venue.CurrentNight.GetOrCreate(member.Id);
                return member.PresenceMode switch
                {
                    PresenceMode.VenueTracking => visible.Contains(Key(member.Name, member.World)),
                    PresenceMode.ManualTimer => record.ManualClockedIn,
                    _ => false,
                };
            })
            .ToList();
        if (readyStaff.Count == 0)
        {
            recoveryVisibleScanCounts[venue.Id] = 0;
            return RecoveryResult.WaitingForVenueReturn;
        }

        var stableVisibleScans = recoveryVisibleScanCounts.GetValueOrDefault(venue.Id) + 1;
        recoveryVisibleScanCounts[venue.Id] = stableVisibleScans;
        if (stableVisibleScans < 2)
            return RecoveryResult.WaitingForVenueReturn;

        var recoveryStart = checkpoint > venue.CurrentNight.StartedUtc ? checkpoint : venue.CurrentNight.StartedUtc;
        var recoveredStaffCount = 0;
        var largestRecovery = 0d;

        foreach (var member in venue.Staff.Where(x => x.Enabled))
        {
            if (!recoveryCandidates.Contains(member.Id)) continue;
            var record = venue.CurrentNight.GetOrCreate(member.Id);
            if (!record.Scheduled || record.ShiftEndedEarly || member.PresenceMode == PresenceMode.NoTimer)
                continue;

            var isVisible = visible.Contains(Key(member.Name, member.World));
            if (member.PresenceMode == PresenceMode.VenueTracking && !isVisible)
                continue;
            if (member.PresenceMode == PresenceMode.ManualTimer && !record.ManualClockedIn)
                continue;

            var recoveredSeconds = config.TrackOnlyDuringScheduledShift
                ? venue.CountAssignedShiftOverlapSeconds(member, recoveryStart, now)
                : venue.CountTrackingDayOverlapSeconds(recoveryStart, now);
            if (recoveredSeconds <= 0)
                continue;

            record.AccruedSeconds += recoveredSeconds;
            record.HasWorked = true;
            record.IsVisible = isVisible;
            record.InCrashGrace = false;
            if (isVisible) record.LastSeenUtc = now;
            recoveredStaffCount++;
            largestRecovery = Math.Max(largestRecovery, recoveredSeconds);
        }

        pendingRecoveryByVenue.Remove(venue.Id);
        pendingRecoveryStaffByVenue.Remove(venue.Id);
        recoveryVisibleScanCounts.Remove(venue.Id);
        venue.CurrentNight.TrackingCheckpointUtc = now;
        venue.CurrentNight.LastCrashRecoveryUtc = now;
        venue.CurrentNight.LastCrashRecoverySeconds = largestRecovery;
        venue.CurrentNight.LastCrashRecoveryStaffCount = recoveredStaffCount;
        persistence.MarkDirty();
        persistence.SaveNow();
        return RecoveryResult.Applied;
    }

    private static HashSet<string> CollectVisiblePlayers()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVisiblePlayer(result, DalamudServices.ObjectTable.LocalPlayer);
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is IPlayerCharacter pc) AddVisiblePlayer(result, pc);
        }
        return result;
    }

    private static void AddVisiblePlayer(HashSet<string> result, IPlayerCharacter? player)
    {
        if (player is null) return;
        var name = player.Name.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var world = player.HomeWorld.Value.Name.ToString();
            if (!string.IsNullOrWhiteSpace(world)) result.Add(Key(name, world));
        }
        catch
        {
            // Ignore actors whose world data is not ready yet.
        }
    }

    private static HashSet<Guid> GetRecoveryCandidates(VenueProfile venue) => venue.Staff
        .Where(member =>
        {
            if (!member.Enabled || member.PresenceMode == PresenceMode.NoTimer) return false;
            var record = venue.CurrentNight.Staff.FirstOrDefault(x => x.StaffId == member.Id);
            return record is not null && record.Scheduled && !record.ShiftEndedEarly &&
                   (record.HasWorked || record.ManualClockedIn || record.LastSeenUtc is not null);
        })
        .Select(x => x.Id)
        .ToHashSet();

    private static string Key(string name, string world) => $"{name.Trim()}@{world.Trim()}";

    public static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 24 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public void Dispose()
    {
        disposed = true;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
    }

    private enum RecoveryResult
    {
        None,
        WaitingForVenueReturn,
        Applied,
        Skipped,
    }
}
