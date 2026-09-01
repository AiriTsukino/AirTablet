using Dalamud.Configuration;

namespace PrizeTrader;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public long LastAmount { get; set; } = 1_000_000;
    public bool SettingsVisible { get; set; }
    public bool AutoAcceptIncomingTrades { get; set; }
}
