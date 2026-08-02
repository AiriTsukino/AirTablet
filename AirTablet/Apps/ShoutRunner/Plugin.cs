using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using ShoutRunner.UI;

namespace ShoutRunner;

internal sealed class Plugin : IDisposable
{
    private static readonly bool RestrictToDevelopmentTester = true;
    private const string DevelopmentTesterName = "Airi Tsukino";
    private const string DevelopmentTesterHomeWorld = "Kraken";
    private const string DevelopmentAccessModal = "ShoutRunner development access##ShoutRunner";

    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly TravelService travel;
    private readonly ChatCommandService chat;
    private readonly RunService runner;
    private readonly CityIconService cityIcons;
    private readonly MainView view;
    private bool settingsOpen;
    private bool developmentAccessMessageRequested;

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
        if (!HasDevelopmentAccess())
            return;
        runner.Tick(persistence.ActiveProfile);
    }

    public void DrawEmbedded()
    {
        if (!HasDevelopmentAccess())
        {
            DrawDevelopmentAccessPage();
            return;
        }
        developmentAccessMessageRequested = false;
        if (settingsOpen)
            view.DrawSettings();
        else
            view.DrawMain();
    }

    public bool ConsumeForegroundRequest() => runner.ConsumeForegroundRequest();

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

    private void OpenSettings()
    {
        settingsOpen = true;
        config.SettingsWindowVisible = true;
        persistence.SaveConfig();
    }

    private bool HasDevelopmentAccess()
    {
        if (!RestrictToDevelopmentTester)
            return true;

        var characterName = string.IsNullOrWhiteSpace(travel.CharacterName)
            ? persistence.LastCharacterName
            : travel.CharacterName;
        var homeWorld = string.IsNullOrWhiteSpace(travel.HomeWorld)
            ? persistence.LastCharacterHomeWorld
            : travel.HomeWorld;
        return characterName.Equals(DevelopmentTesterName, StringComparison.OrdinalIgnoreCase) &&
               homeWorld.Equals(DevelopmentTesterHomeWorld, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawDevelopmentAccessPage()
    {
        ImGui.Spacing();
        ImGui.TextColored(TabletAppTheme.AccentHover, "ShoutRunner");
        ImGui.Spacing();
        ImGui.TextWrapped("ShoutRunner is currently unavailable while development testing is in progress.");

        if (!developmentAccessMessageRequested)
        {
            TabletAppTheme.OpenCenteredModal(DevelopmentAccessModal);
            developmentAccessMessageRequested = true;
        }

        if (!TabletAppTheme.BeginCenteredModal(
                DevelopmentAccessModal,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
            return;

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(420f));
        ImGui.TextWrapped("ShoutRunner is still under development and is not available for general use yet.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        var closeWidth = TabletAppTheme.Px(120f);
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - closeWidth) * 0.5f));
        if (ImGui.Button("Close", new Vector2(closeWidth, 0f)))
            TabletAppTheme.CloseCenteredModal();
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
