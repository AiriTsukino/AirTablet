using Dalamud.Plugin;

namespace AirTablet.Services;

internal interface IAirTabletApp : IDisposable
{
    string InternalName { get; }

    void Initialize(IDalamudPluginInterface pluginInterface);

    void Draw();

    void Tick()
    {
    }

    bool ConsumeForegroundRequest()
    {
        return false;
    }

    bool CanNavigateBack();

    bool NavigateBack();
}
