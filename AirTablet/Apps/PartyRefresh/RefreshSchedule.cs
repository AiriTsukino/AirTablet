namespace PartyRefresh;

internal sealed class RefreshSchedule
{
    private long dueAt;
    private bool wasRecruiting;

    public void Reset(long now, int minutes) => dueAt = now + Math.Clamp(minutes, 1, 55) * 60_000L;
    public long RemainingMilliseconds(long now) => Math.Max(0, dueAt - now);

    public bool IsDue(long now, int minutes, bool recruiting, bool busy)
    {
        if (recruiting && !wasRecruiting) Reset(now, minutes);
        wasRecruiting = recruiting;
        // No one-minute retry deadline that can fire just after a new listing.
        return recruiting && !busy && now >= dueAt;
    }
}
