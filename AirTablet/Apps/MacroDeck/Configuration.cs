using Dalamud.Configuration;

namespace MacroDeck;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public Guid ActiveVenueId { get; set; }
}
