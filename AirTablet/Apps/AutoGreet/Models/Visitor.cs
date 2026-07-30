namespace AutoGreet.Models;

[Serializable]
public sealed class Visitor
{
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastGreetedUtc { get; set; } = DateTimeOffset.MinValue;
    public int TotalVisitCount { get; set; }
    public bool Vip { get; set; }
    public Guid VipTierId { get; set; }
    public bool HasBeenGreeted { get; set; }
    public string Notes { get; set; } = string.Empty;

    public VisitorKey Key => new(CharacterName, World);

    public static Visitor FromKey(VisitorKey key)
    {
        var now = DateTimeOffset.UtcNow;
        return new Visitor { CharacterName = key.Name, World = key.World, FirstSeenUtc = now, LastSeenUtc = now, TotalVisitCount = 0 };
    }
}
