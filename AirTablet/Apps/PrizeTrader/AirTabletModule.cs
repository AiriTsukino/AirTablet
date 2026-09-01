using Dalamud.Plugin;

namespace PrizeTrader;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;
    public string InternalName => "PrizeTrader";
    public void Initialize(IDalamudPluginInterface pluginInterface) => runtime = new Plugin(pluginInterface);
    public void Draw() => runtime?.Draw();
    public void Tick() => runtime?.Tick();
    public bool CanNavigateBack() => runtime?.CanNavigateBack() == true;
    public bool NavigateBack() => runtime?.NavigateBack() == true;
    public string? ConsumeNotification() => runtime?.ConsumeNotification();
    public void Dispose() { runtime?.Dispose(); runtime = null; }
}
