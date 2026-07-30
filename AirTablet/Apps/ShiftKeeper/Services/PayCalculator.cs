using ShiftKeeper.Models;

namespace ShiftKeeper.Services;

public static class PayCalculator
{
    public static long CalculateGrossPay(StaffMember member, NightlyStaffRecord record, VenueProfile venue, Configuration config)
    {
        var rate = Math.Max(0d, member.PayRate);
        var assignedShiftCount = Math.Max(1, venue.GetShifts(member).Count);
        var assignedShiftSeconds = Math.Max(1d, venue.GetAssignedShiftDurationSeconds(member));
        var payableSeconds = CalculatePayableSeconds(
            record.AccruedSeconds,
            config);
        var raw = member.PayType switch
        {
            PayType.Hourly => rate * (payableSeconds / 3600d),
            PayType.PerShift when config.CountUpPerShiftPay && member.PresenceMode != PresenceMode.NoTimer =>
                rate * assignedShiftCount * Math.Clamp(payableSeconds / assignedShiftSeconds, 0d, 1d),
            PayType.PerShift => record.HasWorked ? rate * assignedShiftCount : 0f,
            _ => 0f,
        };

        return RoundToGil(raw, config.PayRoundingIncrement);
    }

    public static long CalculateRemainingDue(StaffMember member, NightlyStaffRecord record, VenueProfile venue, Configuration config) =>
        Math.Max(0, CalculateGrossPay(member, record, venue, config) - record.TotalPaidGil);

    public static long CalculateMaximumShiftPay(StaffMember member, VenueProfile venue, Configuration config)
    {
        var rate = Math.Max(0d, member.PayRate);
        var shiftSeconds = CalculatePayableSeconds(
            Math.Max(1d, venue.GetAssignedShiftDurationSeconds(member)),
            config);
        var assignedShiftCount = Math.Max(1, venue.GetShifts(member).Count);
        var raw = member.PayType == PayType.Hourly ? rate * (shiftSeconds / 3600d) : rate * assignedShiftCount;
        return RoundToGil(raw, config.PayRoundingIncrement);
    }

    public static double CalculatePayableSeconds(
        double accruedSeconds,
        Configuration config)
    {
        var seconds = Math.Max(0d, accruedSeconds);
        var minutes = Math.Clamp(config.PayTimeRoundingMinutes, 0, 1440);
        if (minutes <= 0)
            return seconds;

        var incrementSeconds = minutes * 60d;
        var units = seconds / incrementSeconds;
        var roundedUnits = NormalizeTimeRoundingMode(config.PayTimeRoundingMode) switch
        {
            "Up" => Math.Ceiling(units),
            "Down" => Math.Floor(units),
            _ => Math.Round(units, MidpointRounding.AwayFromZero),
        };
        return roundedUnits * incrementSeconds;
    }

    public static string NormalizeTimeRoundingMode(string? mode) =>
        mode?.Trim().ToUpperInvariant() switch
        {
            "UP" => "Up",
            "DOWN" => "Down",
            _ => "Nearest",
        };

    public static string DescribeTimeRounding(Configuration config)
    {
        var minutes = Math.Max(1, config.PayTimeRoundingMinutes);
        return NormalizeTimeRoundingMode(config.PayTimeRoundingMode) switch
        {
            "Up" => $"up to the next {minutes} min",
            "Down" => $"down to the previous {minutes} min",
            _ => $"nearest {minutes} min",
        };
    }

    public static bool IsWorkComplete(StaffMember member, NightlyStaffRecord record, VenueProfile venue)
    {
        if (record.ShiftEndedEarly) return true;
        if (!record.HasWorked) return false;
        if (member.PresenceMode == PresenceMode.NoTimer) return true;
        var shiftSeconds = venue.GetAssignedShiftDurationSeconds(member);
        return shiftSeconds > 0 && record.AccruedSeconds + 1d >= shiftSeconds;
    }

    private static long RoundToGil(double raw, int configuredIncrement)
    {
        var increment = configuredIncrement > 0 ? configuredIncrement : 1;
        var roundedUnits = Math.Round(Math.Max(0d, raw) / increment, MidpointRounding.AwayFromZero);
        return checked((long)Math.Min(long.MaxValue / (double)increment, roundedUnits) * increment);
    }
}
