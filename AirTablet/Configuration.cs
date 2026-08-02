using System.Numerics;
using Dalamud.Configuration;

namespace AirTablet;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 15;
    public bool SetupCompleted { get; set; }
    public bool TutorialCompleted { get; set; }
    public bool ShowStartupAnimation { get; set; } = true;
    public bool ShowBeforeCharacterLogin { get; set; }
    public bool ShowAirTabOsTooltips { get; set; } = true;
    public bool WindowVisible { get; set; }
    public bool Minimized { get; set; }
    public bool PositionLocked { get; set; }
    public bool AnchorMiniToCollapseCorner { get; set; }
    public string MiniCollapseCorner { get; set; } = "TopLeft";
    public Vector2 Position { get; set; } = new(120, 120);
    public Vector2 MiniPosition { get; set; } = new(120, 120);
    public bool MiniPositionInitialized { get; set; }
    public string Theme { get; set; } = "Purple";
    public string TabletSize { get; set; } = "Large";
    public string WallpaperPath { get; set; } = string.Empty;
    public float WallpaperOpacity { get; set; } = 0.55f;
    public bool ShowBattery { get; set; } = true;
    public bool Use24HourClock { get; set; } = true;
    public string LastReadChangelogVersion { get; set; } = string.Empty;
    public List<string> AppOrder { get; set; } = [];
    public bool AppSelectionInitialized { get; set; }
    public List<string> EnabledApps { get; set; } = [];
    public List<string> DisabledApps { get; set; } =
    [
        "AutoGreet",
        "BarManager",
        "GambaAssistant",
        "RaffleManager",
        "ShiftKeeper",
        "ShopHelper",
        "ShoutRunner",
    ];
    public string PluginConfigSourceDirectory { get; set; } =
        DefaultPluginConfigSourceDirectory;
    public int OriginalConfigMigrationCount { get; set; }
    public string CatalogUrl { get; set; } =
        "https://raw.githubusercontent.com/AiriTsukino/AiriPluginHub/main/pluginmaster.json";

    public static string DefaultPluginConfigSourceDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "pluginConfigs");
}
