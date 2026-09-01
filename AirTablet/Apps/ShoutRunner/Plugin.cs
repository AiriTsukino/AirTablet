using Dalamud.Plugin;
using ShoutRunner.UI;

namespace ShoutRunner;

internal sealed class Plugin : IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly TravelService travel;
    private readonly ChatCommandService chat;
    private readonly RunService runner;
    private readonly CityIconService cityIcons;
    private readonly MainView view;
    private bool settingsOpen;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        persistence = new PersistenceService(config);
        travel = new TravelService();
        chat = new ChatCommandService();
        runner = new RunService(persistence, travel, chat);
        cityIcons = new CityIconService();
        settingsOpen = config.SettingsWindowVisible;
        view = new MainView(config, persistence, runner, travel, cityIcons, OpenSettings);
    }

    public bool KeepTabletVisibleDuringTravel => runner.KeepTabletVisibleDuringTravel;

    public void Tick()
    {
        persistence.RememberCharacter(travel.CharacterName, travel.HomeWorld, travel.CurrentWorld);
        runner.Tick(persistence.ActiveProfile);
    }

    public void DrawEmbedded()
    {
        if (settingsOpen)
            view.DrawSettings();
        else
            view.DrawMain();
    }

    public bool ConsumeForegroundRequest() => runner.ConsumeForegroundRequest();

    public bool ConsumeHomeRequest() => false;

    public bool CanNavigateBackEmbedded() => settingsOpen;

    public bool NavigateBackEmbedded()
    {
        if (!settingsOpen)
            return false;
        settingsOpen = false;
        config.SettingsWindowVisible = false;
        persistence.SaveConfig();
        return true;
    }

    public IReadOnlyList<AirTablet.Services.ControlCenterWidget> GetControlCenterWidgets() =>
    [
        new(
            "shoutrunner.progress",
            "ShoutRunner",
            "Shout route",
            "Current advertising route progress and task.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new(
                runner.IsPaused
                    ? $"Paused · {runner.CompletedStops:N0}/{runner.TotalStops:N0}"
                    : runner.IsRunning
                        ? $"{runner.CompletedStops:N0}/{runner.TotalStops:N0} stops"
                        : runner.Phase == RunPhase.Completed
                            ? "Run complete"
                            : "Not running",
                runner.IsRunning || runner.IsPaused ? runner.CurrentTask : runner.Status)),
    ];

    private void OpenSettings()
    {
        settingsOpen = true;
        config.SettingsWindowVisible = true;
        persistence.SaveConfig();
    }

    public void Dispose()
    {
        runner.Dispose();
        cityIcons.Dispose();
        persistence.SaveNow();
    }
}
