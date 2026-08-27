using Dalamud.Plugin;

namespace PartyRefresh;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;
    public string InternalName => "PartyRefresh";
    public void Initialize(IDalamudPluginInterface pluginInterface) => runtime = new Plugin(pluginInterface);
    public void Draw() => runtime?.Draw();
    public void Tick() => runtime?.Tick();
    public bool CanNavigateBack() => runtime?.CanNavigateBack() ?? false;
    public bool NavigateBack() => runtime?.NavigateBack() ?? false;
    public string? ConsumeNotification() => runtime?.ConsumeNotification();
    public IReadOnlyList<AirTablet.Services.ControlCenterWidget> GetControlCenterWidgets() =>
        runtime?.GetControlCenterWidgets() ?? [];
    public void Dispose()
    {
        runtime?.Dispose();
        runtime = null;
    }
}
