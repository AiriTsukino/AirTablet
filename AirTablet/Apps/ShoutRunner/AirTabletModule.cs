using Dalamud.Plugin;

namespace ShoutRunner;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;

    public string InternalName => "ShoutRunner";
    public bool KeepTabletVisibleDuringTravel => runtime?.KeepTabletVisibleDuringTravel ?? false;

    public void Initialize(IDalamudPluginInterface pluginInterface) =>
        runtime = new Plugin(pluginInterface);

    public void Draw() => runtime?.DrawEmbedded();
    public IReadOnlyList<AirTablet.Services.ControlCenterWidget> GetControlCenterWidgets() =>
        runtime?.GetControlCenterWidgets() ?? [];
    public void Tick() => runtime?.Tick();
    public bool ConsumeForegroundRequest() => runtime?.ConsumeForegroundRequest() ?? false;
    public bool ConsumeHomeRequest() => runtime?.ConsumeHomeRequest() ?? false;
    public bool CanNavigateBack() => runtime?.CanNavigateBackEmbedded() ?? false;
    public bool NavigateBack() => runtime?.NavigateBackEmbedded() ?? false;

    public void Dispose()
    {
        runtime?.Dispose();
        runtime = null;
    }
}
