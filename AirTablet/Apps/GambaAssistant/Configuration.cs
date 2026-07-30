using Dalamud.Configuration;
using GambaAssistant.Models.Ledger;

namespace GambaAssistant;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool WindowVisible { get; set; }
    public bool SettingsWindowVisible { get; set; }
    public Guid ActiveProfileId { get; set; }
    public bool DemoModeEnabled { get; set; }
    public OverlaySettings Overlay { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public DeathRollSettings DeathRoll { get; set; } = new();
}

[Serializable]
public sealed class GeneralSettings
{
    public string DealerMode { get; set; } = "Venue bank";
    public float ChatQueueDelaySeconds { get; set; } = 1.0f;
    public int UndoLimit { get; set; } = 10;
    public string ExportDirectory { get; set; } = string.Empty;
    public bool AstrologianImmersionEnabled { get; set; } = false;
    public bool AstrologianAstralDrawOnDealEnabled { get; set; } = false;
    public bool AstrologianKeepBattleModeOnEnabled { get; set; } = false;
    public bool AutomaticTradeDetectionEnabled { get; set; } = true;
    public bool BlackjackSessionAutosaveEnabled { get; set; } = true;
    public bool DeathRollSessionAutosaveEnabled { get; set; } = true;
}

[Serializable]
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool Compact { get; set; } = true;
    public int PlayerPanelColumns { get; set; } = 1;
    public float TextScale { get; set; } = 1.0f;
    public float VerticalOffset { get; set; } = 1.15f;
    public Dictionary<string, float> PlayerHeightOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public float MaxRenderDistance { get; set; } = 30f;
    public float BackgroundOpacity { get; set; } = 0.55f;
    public bool OpaqueBackground { get; set; }
    public bool ShowOnlyTableMembers { get; set; } = true;
    public bool UseDrawnOverhead { get; set; } = false;
}

[Serializable]
public sealed class DeathRollSettings
{
    public int MaxPlayers { get; set; } = 32;
    public float BracketZoom { get; set; } = 1.0f;
    public bool BracketWindowOpen { get; set; } = false;
    public string ChatChannel { get; set; } = "party";
    public float ChatBroadcastDelaySeconds { get; set; } = 1.5f;
    public bool DisableChatBroadcasts { get; set; } = false;
    public bool AnnounceNextTurnAfterRoll { get; set; } = false;
    public bool RequireDiceRollsInPartyChat { get; set; } = true;
    public bool JoinBroadcastActive { get; set; } = false;
    public string OpeningZeroRollBehavior { get; set; } = "eliminate";
}
