namespace MacroDeck;

public enum DeckEntryKind
{
    Macro,
    Folder,
}

public enum MacroChatChannel
{
    Say,
    Shout,
    Yell,
    Echo,
}

public sealed class DeckEntry
{
    public const int MaxTitleLength = 128;

    public Guid Id { get; set; } = Guid.NewGuid();
    public int Slot { get; set; }
    public DeckEntryKind Kind { get; set; }
    public string Title { get; set; } = "New Macro";
    public string ImagePath { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    // Legacy prototype fields are retained for one-time migration into Script.
    public MacroChatChannel Channel { get; set; } = MacroChatChannel.Echo;
    public string Message { get; set; } = string.Empty;
    public string EmoteCommand { get; set; } = string.Empty;
    public List<DeckEntry> Children { get; set; } = [];

    public static string NormalizeTitle(string? title, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();
        return value.Length <= MaxTitleLength ? value : value[..MaxTitleLength];
    }
}

public sealed class VenueProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default Venue";
    public List<DeckEntry> Buttons { get; set; } = [];
    public List<Guid?> ControlCenterSlots { get; set; } = [null, null, null, null];
    public Dictionary<string, List<Guid?>> ControlCenterPads { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static VenueProfile Create(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "New Venue" : name.Trim(),
    };
}

public sealed class VenueExportFile
{
    public int FormatVersion { get; set; } = 3;
    public string ExportedBy { get; set; } = "MacroDeck";
    public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public VenueProfile Venue { get; set; } = VenueProfile.Create("Imported Venue");
}

internal sealed class ProfileStore
{
    public int Version { get; set; } = 1;
    public List<VenueProfile> Venues { get; set; } = [];
}
