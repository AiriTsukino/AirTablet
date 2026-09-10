namespace PartyRefresh;

internal sealed class RefreshSchedule
{
    private long dueAt;

    public void Reset(long now, int minutes) => dueAt = now + Math.Clamp(minutes, 1, 55) * 60_000L;
    public void Restore(long now, long remainingMilliseconds) => dueAt = now + Math.Max(0, remainingMilliseconds);
    public long RemainingMilliseconds(long now) => Math.Max(0, dueAt - now);

    public bool IsDue(long now, bool recruiting, bool busy) => recruiting && !busy && now >= dueAt;
}
