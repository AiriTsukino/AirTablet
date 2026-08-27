using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using ShoutRunner.UI;

namespace ShoutRunner;

internal sealed class Plugin : IDisposable
{
    private const string EarlyAccessModal = "ShoutRunner early access##ShoutRunner";
    private const string ShoutRunnerVersion = "1.0.42.0";

    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly TravelService travel;
    private readonly ChatCommandService chat;
    private readonly RunService runner;
    private readonly CityIconService cityIcons;
    private readonly MainView view;
    private bool settingsOpen;
    private bool earlyAccessMessageRequested;
    private bool homeRequested;

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

        if (!string.Equals(
                config.LastAcknowledgedEarlyAccessVersion,
                ShoutRunnerVersion,
                StringComparison.OrdinalIgnoreCase))
            DrawEarlyAccessPopup();
        else
            earlyAccessMessageRequested = false;
    }

    public bool ConsumeForegroundRequest() => runner.ConsumeForegroundRequest();

    public bool ConsumeHomeRequest()
    {
        if (!homeRequested)
            return false;
        homeRequested = false;
        return true;
    }

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

    private void DrawEarlyAccessPopup()
    {
        if (!earlyAccessMessageRequested)
        {
            TabletAppTheme.OpenCenteredModal(EarlyAccessModal);
            earlyAccessMessageRequested = true;
        }

        if (!TabletAppTheme.BeginCenteredModal(
                EarlyAccessModal,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(420f));
        ImGui.TextWrapped("ShoutRunner is still in active development and some travel steps may not work reliably yet. Please report bugs in the community Discord.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        var buttonWidth = TabletAppTheme.Px(150f);
        var buttonGap = TabletAppTheme.Px(8f);
        var buttonsWidth = buttonWidth * 2f + buttonGap;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - buttonsWidth) * 0.5f));
        if (ImGui.Button("Acknowledge", new Vector2(buttonWidth, 0f)))
        {
            config.LastAcknowledgedEarlyAccessVersion = ShoutRunnerVersion;
            persistence.SaveConfig();
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine(0f, buttonGap);
        if (ImGui.Button("Return home", new Vector2(buttonWidth, 0f)))
        {
            homeRequested = true;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.Dummy(new Vector2(1f, TabletAppTheme.Px(8f)));
        TabletAppTheme.EndCenteredModal();
    }

    public void Dispose()
    {
        runner.Dispose();
        cityIcons.Dispose();
        persistence.SaveNow();
    }
}
