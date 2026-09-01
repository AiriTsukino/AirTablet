using Dalamud.Plugin;

namespace WardrobeManager;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;
    public string InternalName => "WardrobeManager";
    public void Initialize(IDalamudPluginInterface pluginInterface) => runtime = new Plugin(pluginInterface);
    public void Draw() => runtime?.Draw();
    public void Tick() => runtime?.Tick();
    public bool CanNavigateBack() => runtime?.CanNavigateBack() ?? false;
    public bool NavigateBack() => runtime?.NavigateBack() ?? false;
    public bool ConsumeForegroundRequest() => runtime?.ConsumeForegroundRequest() ?? false;
    public string? ConsumeNotification() => runtime?.ConsumeNotification();
    public void Dispose() { runtime?.Dispose(); runtime = null; }
}
