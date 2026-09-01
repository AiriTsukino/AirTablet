using Dalamud.Configuration;

namespace ShiftKeeper;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;
    public bool WindowVisible { get; set; }
    public bool SettingsWindowVisible { get; set; }
    public Guid ActiveVenueId { get; set; }
    public int CrashGraceMinutes { get; set; } = 5;
    public bool TrackOnlyDuringScheduledShift { get; set; } = true;
    public int PayRoundingIncrement { get; set; }
    public int PayTimeRoundingMinutes { get; set; }
    public bool UseCustomPayTimeRounding { get; set; }
    public string PayTimeRoundingMode { get; set; } = "Nearest";
    public bool CountUpPerShiftPay { get; set; }
    public bool HostCrashRecoveryEnabled { get; set; } = true;
    public bool AutoDetectTradePayments { get; set; } = true;
    public bool FiredrillMode { get; set; }
}
