using Dalamud.Configuration;
using System.Numerics;

namespace MacroDeck;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public Guid ActiveVenueId { get; set; }
    public bool PopoutEnabled { get; set; }
    public bool PopoutPositionLocked { get; set; }
    public bool PopoutTooltipsEnabled { get; set; } = true;
    public bool PopoutUseCustomImages { get; set; }
    public float PopoutScale { get; set; } = 0.85f;
    public Vector2 PopoutPosition { get; set; } = new(120f, 120f);
    public bool PopoutPositionInitialized { get; set; }
}
