using Dalamud.Plugin;

namespace AutoGreet;

internal sealed class AirTabletModule : AirTablet.Services.IAirTabletApp
{
    private Plugin? runtime;

    public string InternalName => "AutoGreet";

    public void Initialize(IDalamudPluginInterface pluginInterface) =>
        runtime = new Plugin(pluginInterface, true);

    public void Draw() => runtime?.DrawEmbedded();

    public bool CanNavigateBack() => runtime?.CanNavigateBackEmbedded() ?? false;

    public bool NavigateBack() => runtime?.NavigateBackEmbedded() ?? false;

    public void Dispose()
    {
        runtime?.Dispose();
        runtime = null;
    }
}
