using Dalamud.Plugin;

namespace ShopHelper;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;

    public string InternalName => "ShopHelper";

    public void Initialize(IDalamudPluginInterface pluginInterface) =>
        runtime = new Plugin(pluginInterface, true);

    public void Draw() => runtime?.DrawEmbedded();

    public void Tick() => runtime?.TickEmbedded();

    public bool ConsumeForegroundRequest() =>
        runtime?.ConsumeForegroundRequestEmbedded() ?? false;

    public bool CanNavigateBack() => runtime?.CanNavigateBackEmbedded() ?? false;

    public bool NavigateBack() => runtime?.NavigateBackEmbedded() ?? false;

    public void Dispose()
    {
        runtime?.Dispose();
        runtime = null;
    }
}
