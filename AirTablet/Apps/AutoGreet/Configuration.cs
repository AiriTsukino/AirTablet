using Dalamud.Configuration;

namespace AutoGreet;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;
    public bool WindowVisible { get; set; }
    public bool SettingsWindowVisible { get; set; }
    public bool AutoGreetEnabled { get; set; }
    public bool DoorbellSoundEnabled { get; set; } = true;
    public float DoorbellVolume { get; set; } = 0.25f;
    public string CustomDoorbellSoundPath { get; set; } = string.Empty;
    public bool ChatNotificationsEnabled { get; set; } = true;
    public bool ChatNotificationsForBlacklistedEnabled { get; set; } = true;
    public bool LeaveChatNotificationsEnabled { get; set; } = false;
    public bool ResumePreviousEmoteEnabled { get; set; } = false;
    public string ResumeEmoteCommand { get; set; } = string.Empty;
    public float ResumeEmoteDelaySeconds { get; set; } = 4.0f;
    public bool UntargetAfterGreeting { get; set; } = false;
    public bool WaitForVisibleTargetBeforeEmote { get; set; } = true;
    public float EmoteTargetWaitSeconds { get; set; } = 8.0f;
    public float GreetingStartDelaySeconds { get; set; } = 3.0f;
    public float QueueDelaySeconds { get; set; } = 3.0f;
    public bool GreetingTimerEnabled { get; set; } = false;
    public int GreetingTimerMinutes { get; set; } = 120;
    public Guid ActiveVenueId { get; set; }
    public bool ActiveVenueDisabled { get; set; }
    public bool MonitorWhenNoVenueSelected { get; set; } = false;
    public bool ShowBlacklistedInActiveVisitors { get; set; } = true;
    public List<uint> CustomHousingTerritories { get; set; } = [];
}
