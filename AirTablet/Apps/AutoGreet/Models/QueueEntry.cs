namespace AutoGreet.Models;

public enum QueueEntryStatus
{
    Waiting,
    Running,
    Completed,
    Cancelled,
    Failed
}

[Serializable]
public sealed class QueueEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public VisitorKey Visitor { get; set; }
    public DateTimeOffset EnqueuedUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid GreetingProfileId { get; set; }
    public GreetingCategory Category { get; set; }
    public Guid MacroOverrideId { get; set; }
    public Guid CustomRegionRouteId { get; set; }
    public bool AllowDetachedCustomGreeting { get; set; }
    public QueueEntryStatus Status { get; set; } = QueueEntryStatus.Waiting;
    public string StatusText { get; set; } = "Waiting";
}
