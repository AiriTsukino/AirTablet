namespace AutoGreet.Models;

[Serializable]
public sealed class SessionVisitorState
{
    public VisitorKey Key { get; set; }
    public DateTimeOffset EnteredUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenBeforeCurrentEntryUtc { get; set; } = DateTimeOffset.MinValue;
    public bool Present { get; set; } = true;
    public bool ReturningThisSession { get; set; }
    public bool HereWhenArrived { get; set; }
}

[Serializable]
public sealed class NightlySnapshot
{
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int TotalVisitors { get; set; }
    public int GreetedCount { get; set; }
    public int UngreetedCount { get; set; }
}

[Serializable]
public sealed class SessionData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<SessionVisitorState> NightlyVisitors { get; set; } = [];
    public List<VisitorKey> Greeted { get; set; } = [];
    public List<VisitorKey> Ungreeted { get; set; } = [];
    public List<VisitorKey> Skipped { get; set; } = [];
    public List<NightlySnapshot> Snapshots { get; set; } = [];
    public Dictionary<Guid, List<VisitorKey>> CustomRegionGreetings { get; set; } = [];

    public void Reset()
    {
        Id = Guid.NewGuid();
        StartedUtc = DateTimeOffset.UtcNow;
        NightlyVisitors.Clear();
        Greeted.Clear();
        Ungreeted.Clear();
        Skipped.Clear();
        CustomRegionGreetings.Clear();
    }
}
