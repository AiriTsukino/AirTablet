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

    bool KeepTabletVisibleDuringTravel => false;

    bool ConsumeForegroundRequest()
    {
        return false;
    }

    bool ConsumeHomeRequest()
    {
        return false;
    }

    string? ConsumeNotification()
    {
        return null;
    }

    IReadOnlyList<ControlCenterWidget> GetControlCenterWidgets() => [];

    bool CanNavigateBack();

    bool NavigateBack();
}
