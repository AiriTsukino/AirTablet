using Dalamud.Plugin;

namespace BarManager;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;

    public string InternalName => "BarManager";

    public void Initialize(IDalamudPluginInterface pluginInterface) =>
        runtime = new Plugin(pluginInterface, true);

    public void Draw() => runtime?.DrawEmbedded();

    public IReadOnlyList<AirTablet.Services.ControlCenterWidget> GetControlCenterWidgets() =>
        runtime?.GetControlCenterWidgets() ?? [];

    public bool CanNavigateBack() => runtime?.CanNavigateBackEmbedded() ?? false;

    public bool NavigateBack() => runtime?.NavigateBackEmbedded() ?? false;

    public void Dispose()
    {
        runtime?.Dispose();
        runtime = null;
    }
}
