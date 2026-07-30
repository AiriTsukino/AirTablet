namespace ShiftKeeper.Models;

public enum PayType
{
    Hourly,
    PerShift,
}

public enum PresenceMode
{
    VenueTracking,
    ManualTimer,
    NoTimer,
}

public static class CommonRoles
{
    public static readonly string[] All =
    [
        "Owner", "Manager", "Greeter", "Host", "Bartender", "Server", "Dancer", "Courtesan",
        "Gamba Dealer", "Blackjack Dealer", "Game Master", "DJ", "Shout Runner", "Photographer",
        "Security", "Bouncer", "Performer", "Other"
    ];
}

public sealed class StaffMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public string Role { get; set; } = "Greeter";
    public string CustomRole { get; set; } = string.Empty;
    public PayType PayType { get; set; } = PayType.Hourly;
    public float PayRate { get; set; }
    public PresenceMode PresenceMode { get; set; } = PresenceMode.VenueTracking;
    // ShiftId is retained so existing saved profiles and exports migrate cleanly.
    public Guid ShiftId { get; set; }
    public List<Guid> ShiftIds { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string Notes { get; set; } = string.Empty;

    public string DisplayRole => Role == "Other" && !string.IsNullOrWhiteSpace(CustomRole) ? CustomRole.Trim() : Role;
    public string TellRecipient => $"{Name.Trim()}@{World.Trim()}";
}

public sealed class ShiftDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Main Shift";
    public int StartMinutes { get; set; } = 20 * 60;
    public int EndMinutes { get; set; } = 24 * 60;

    public bool Contains(DateTime localNow)
    {
        var minute = localNow.Hour * 60 + localNow.Minute;
        var start = Math.Clamp(StartMinutes, 0, 1440);
        var end = Math.Clamp(EndMinutes, 0, 1440);
        if (IsFullDay(start, end)) return true;
        if (start == 1440) return minute < end;
        return end > start ? minute >= start && minute < end : minute >= start || minute < end;
    }

    public string RangeText => $"{FormatMinutes(StartMinutes)}–{FormatMinutes(EndMinutes)}";

    public double DurationSeconds
    {
        get
        {
            var start = Math.Clamp(StartMinutes, 0, 1440);
            var end = Math.Clamp(EndMinutes, 0, 1440);
            var minutes = IsFullDay(start, end)
                ? 1440
                : start == 1440 ? end
                : end > start ? end - start : 1440 - start + end;
            return minutes * 60d;
        }
    }

    public double CountOverlapSeconds(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc) return 0;

        return GetOverlappingUtcIntervals(startUtc, endUtc).Sum(x => (x.End - x.Start).TotalSeconds);
    }

    public DayOfWeek GetTrackingDay(DateTime localNow)
    {
        var minute = localNow.Hour * 60 + localNow.Minute;
        var start = Math.Clamp(StartMinutes, 0, 1440);
        var end = Math.Clamp(EndMinutes, 0, 1440);
        var fullDay = IsFullDay(start, end);

        if (start == 1440) return localNow.AddDays(-1).DayOfWeek;
        if (fullDay)
            return start > 0 && minute < start ? localNow.AddDays(-1).DayOfWeek : localNow.DayOfWeek;
        if (end < start && minute < end)
            return localNow.AddDays(-1).DayOfWeek;
        return localNow.DayOfWeek;
    }

    internal IEnumerable<(DateTimeOffset Start, DateTimeOffset End, DayOfWeek TrackingDay)> GetOverlappingUtcIntervals(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc) yield break;

        var zone = TimeZoneInfo.Local;
        var firstDate = TimeZoneInfo.ConvertTime(startUtc, zone).Date.AddDays(-1);
        var lastDate = TimeZoneInfo.ConvertTime(endUtc, zone).Date;
        var startMinutes = Math.Clamp(StartMinutes, 0, 1440);
        var endMinutes = Math.Clamp(EndMinutes, 0, 1440);

        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var localStart = date.AddMinutes(startMinutes);
            DateTime localEnd;
            if (IsFullDay(startMinutes, endMinutes))
                localEnd = localStart.AddDays(1);
            else if (endMinutes > startMinutes)
                localEnd = date.AddMinutes(endMinutes);
            else
                localEnd = date.AddDays(1).AddMinutes(endMinutes);

            var intervalStart = new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)).ToUniversalTime();
            var intervalEnd = new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)).ToUniversalTime();
            var overlapStart = intervalStart > startUtc ? intervalStart : startUtc;
            var overlapEnd = intervalEnd < endUtc ? intervalEnd : endUtc;
            if (overlapEnd > overlapStart) yield return (overlapStart, overlapEnd, date.DayOfWeek);
        }
    }

    public static string FormatMinutes(int value)
    {
        value = Math.Clamp(value, 0, 1440);
        if (value == 1440) return "12:00 AM";
        return DateTime.Today.AddMinutes(value).ToString("h:mm tt");
    }

    private static bool IsFullDay(int start, int end) =>
        start == end || start == 0 && end == 1440 || start == 1440 && end == 0;
}

public sealed class NightlyStaffRecord
{
    public Guid StaffId { get; set; }
    public bool Scheduled { get; set; } = true;
    public bool ManualClockedIn { get; set; }
    public bool HasWorked { get; set; }
    public bool IsVisible { get; set; }
    public bool InCrashGrace { get; set; }
    public double AccruedSeconds { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public bool Paid { get; set; }
    public DateTimeOffset? PaidUtc { get; set; }
    public float PaidAmount { get; set; }
    public long TotalPaidGil { get; set; }
    public bool PaidAutomatically { get; set; }
    public DateTimeOffset? LastTradePaymentUtc { get; set; }
    public long LastTradePaymentGil { get; set; }
    public bool ShiftEndedEarly { get; set; }
    public DateTimeOffset? ShiftEndedUtc { get; set; }
}

public sealed class NightSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; set; }
    public DateTimeOffset TrackingCheckpointUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? LastCrashRecoveryUtc { get; set; }
    public double LastCrashRecoverySeconds { get; set; }
    public int LastCrashRecoveryStaffCount { get; set; }
    public List<NightlyStaffRecord> Staff { get; set; } = [];

    public NightlyStaffRecord GetOrCreate(Guid staffId)
    {
        var record = Staff.FirstOrDefault(x => x.StaffId == staffId);
        if (record is not null) return record;
        record = new NightlyStaffRecord { StaffId = staffId };
        Staff.Add(record);
        return record;
    }
}

public sealed class VenueProfile
{
    public static readonly DayOfWeek[] WeekDayOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Venue";
    public string CurrencyLabel { get; set; } = "gil";
    public List<DayOfWeek> TrackingDays { get; set; } = [.. WeekDayOrder];
    public List<ShiftDefinition> Shifts { get; set; } = [];
    public List<StaffMember> Staff { get; set; } = [];
    public NightSession CurrentNight { get; set; } = new();
    public List<NightSession> NightHistory { get; set; } = [];

    public static VenueProfile Create(string name)
    {
        var shift = new ShiftDefinition();
        return new VenueProfile { Name = name, Shifts = [shift] };
    }

    public ShiftDefinition? GetShift(Guid id) => Shifts.FirstOrDefault(x => x.Id == id) ?? Shifts.FirstOrDefault();

    public List<ShiftDefinition> GetShifts(StaffMember member)
    {
        NormalizeShiftAssignments(member);
        var assignedIds = member.ShiftIds.ToHashSet();
        return Shifts.Where(x => assignedIds.Contains(x.Id)).ToList();
    }

    public void NormalizeShiftAssignments(StaffMember member)
    {
        member.ShiftIds ??= [];
        var validIds = Shifts.Select(x => x.Id).ToHashSet();
        member.ShiftIds = member.ShiftIds
            .Where(x => x != Guid.Empty && validIds.Contains(x))
            .Distinct()
            .ToList();

        if (member.ShiftIds.Count == 0 && member.ShiftId != Guid.Empty && validIds.Contains(member.ShiftId))
            member.ShiftIds.Add(member.ShiftId);
        if (member.ShiftIds.Count == 0 && Shifts.Count > 0)
            member.ShiftIds.Add(Shifts[0].Id);

        member.ShiftId = member.ShiftIds.FirstOrDefault();
    }

    public void NormalizeTrackingDays()
    {
        TrackingDays ??= [];
        TrackingDays = WeekDayOrder.Where(TrackingDays.Contains).ToList();
        if (TrackingDays.Count == 0) TrackingDays = [.. WeekDayOrder];
    }

    public bool IsTrackingDay(DayOfWeek day)
    {
        NormalizeTrackingDays();
        return TrackingDays.Contains(day);
    }

    public bool IsTrackingTime(DateTime localNow)
    {
        if (IsTrackingDay(localNow.DayOfWeek)) return true;
        return Shifts.Any(x => x.Contains(localNow) && IsTrackingDay(x.GetTrackingDay(localNow)));
    }

    public string GetShiftSummary(StaffMember member) => string.Join(", ", GetShifts(member).Select(x => x.Name));

    public bool IsInAssignedShift(StaffMember member, DateTime localNow)
    {
        var shifts = GetShifts(member);
        return shifts.Count == 0
            ? IsTrackingDay(localNow.DayOfWeek)
            : shifts.Any(x => x.Contains(localNow) && IsTrackingDay(x.GetTrackingDay(localNow)));
    }

    public double GetAssignedShiftDurationSeconds(StaffMember member)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var shift in GetShifts(member))
        {
            var start = Math.Clamp(shift.StartMinutes, 0, 1440);
            var end = Math.Clamp(shift.EndMinutes, 0, 1440);
            if (start == end || start == 0 && end == 1440 || start == 1440 && end == 0)
            {
                ranges.Add((start, start + 1440));
            }
            else if (start == 1440)
            {
                ranges.Add((start, start + end));
            }
            else if (end > start)
            {
                ranges.Add((start, end));
            }
            else
            {
                ranges.Add((start, 1440 + end));
            }
        }

        if (ranges.Count == 0) return 0;
        var ordered = ranges.OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        var totalMinutes = 0;
        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].End;
        foreach (var range in ordered.Skip(1))
        {
            if (range.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, range.End);
                continue;
            }

            totalMinutes += currentEnd - currentStart;
            currentStart = range.Start;
            currentEnd = range.End;
        }
        totalMinutes += currentEnd - currentStart;
        return totalMinutes * 60d;
    }

    public double CountAssignedShiftOverlapSeconds(StaffMember member, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var intervals = GetShifts(member)
            .SelectMany(x => x.GetOverlappingUtcIntervals(startUtc, endUtc))
            .Where(x => IsTrackingDay(x.TrackingDay))
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToList();
        if (intervals.Count == 0) return 0;

        var total = 0d;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd) currentEnd = interval.End;
                continue;
            }

            total += (currentEnd - currentStart).TotalSeconds;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }
        return total + (currentEnd - currentStart).TotalSeconds;
    }

    public double CountTrackingDayOverlapSeconds(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc) return 0;

        var zone = TimeZoneInfo.Local;
        var firstDate = TimeZoneInfo.ConvertTime(startUtc, zone).Date;
        var lastDate = TimeZoneInfo.ConvertTime(endUtc, zone).Date;
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            if (!IsTrackingDay(date.DayOfWeek)) continue;
            var nextDate = date.AddDays(1);
            var dayStart = new DateTimeOffset(date, zone.GetUtcOffset(date)).ToUniversalTime();
            var dayEnd = new DateTimeOffset(nextDate, zone.GetUtcOffset(nextDate)).ToUniversalTime();
            var overlapStart = dayStart > startUtc ? dayStart : startUtc;
            var overlapEnd = dayEnd < endUtc ? dayEnd : endUtc;
            if (overlapEnd > overlapStart) intervals.Add((overlapStart, overlapEnd));
        }

        intervals.AddRange(Shifts
            .SelectMany(x => x.GetOverlappingUtcIntervals(startUtc, endUtc))
            .Where(x => IsTrackingDay(x.TrackingDay))
            .Select(x => (x.Start, x.End)));
        if (intervals.Count == 0) return 0;

        var ordered = intervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
        var total = 0d;
        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].End;
        foreach (var interval in ordered.Skip(1))
        {
            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd) currentEnd = interval.End;
                continue;
            }

            total += (currentEnd - currentStart).TotalSeconds;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }
        return total + (currentEnd - currentStart).TotalSeconds;
    }
}

public sealed class VenueExportFile
{
    public int FormatVersion { get; set; } = 3;
    public string ExportedBy { get; set; } = "ShiftKeeper";
    public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public VenueProfile Venue { get; set; } = VenueProfile.Create("Imported Venue");
}
