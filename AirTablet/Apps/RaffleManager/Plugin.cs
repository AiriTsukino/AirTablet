using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using RaffleManager.Services;
using RaffleManager.UI;

namespace RaffleManager;

internal sealed class Plugin : IDisposable
{
    private const string CommandName = "/rafflemanager";
    private const string SettingsCommandName = "/rafflemanagersettings";

    private readonly WindowSystem windowSystem = new("RaffleManager");
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly RaffleService raffle;
    private readonly SoundService sound;
    private readonly LogoService logo;
    private readonly ChatCommandService chatCommands;
    private readonly AnnouncementService announcements;
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly bool tabletHosted;

    public Plugin(IDalamudPluginInterface pluginInterface) : this(pluginInterface, false)
    {
    }

    internal Plugin(IDalamudPluginInterface pluginInterface, bool tabletHosted)
    {
        this.tabletHosted = tabletHosted;
        DalamudServices.Initialize(pluginInterface);
        config = DalamudServices.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var migrateDefaultSplitter = config.Version < 4;
        persistence = new PersistenceService(config);
        if (migrateDefaultSplitter)
        {
            foreach (var profile in config.VenueProfiles.Values)
            {
                if (MathF.Abs(profile.MainWindowLeftPanelRatio - 0.40f) < 0.001f ||
                    MathF.Abs(profile.MainWindowLeftPanelRatio - 0.37f) < 0.001f)
                    profile.MainWindowLeftPanelRatio = 0.33f;
            }
            config.Version = 4;
            persistence.SaveNow();
        }
        raffle = new RaffleService(config, persistence);
        sound = new SoundService(config);
        logo = new LogoService(config);
        chatCommands = new ChatCommandService();
        announcements = new AnnouncementService(config, chatCommands);
        mainWindow = new MainWindow(config, persistence, raffle, sound, logo, announcements, OpenSettingsWindow) { IsOpen = config.WindowVisible };
        settingsWindow = new SettingsWindow(config, persistence, logo, announcements) { IsOpen = config.SettingsWindowVisible };

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);

        if (tabletHosted)
        {
            mainWindow.IsOpen = true;
            settingsWindow.IsOpen = false;
            config.WindowVisible = true;
            config.SettingsWindowVisible = false;
        }
        else
        {
            DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = "Toggle RaffleManager main window." });
            DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand) { HelpMessage = "Toggle RaffleManager settings window." });
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        }
        persistence.SaveNow();
    }

    private void OnCommand(string command, string arguments)
    {
        var mode = arguments.Trim();
        if (mode.Equals("probe", StringComparison.OrdinalIgnoreCase)) return;
        if (mode.Equals("open", StringComparison.OrdinalIgnoreCase)) OpenMainWindow();
        else ToggleMainUi();
    }

    private void OnSettingsCommand(string command, string arguments)
    {
        var mode = arguments.Trim();
        if (mode.Equals("probe", StringComparison.OrdinalIgnoreCase)) return;
        if (mode.Equals("open", StringComparison.OrdinalIgnoreCase)) OpenSettingsWindow();
        else ToggleConfigUi();
    }

    private void OpenMainWindow()
    {
        config.WindowVisible = true;
        mainWindow.IsOpen = true;
        persistence.SaveNow();
    }

    private void OpenSettingsWindow()
    {
        config.SettingsWindowVisible = true;
        settingsWindow.IsOpen = true;
        persistence.SaveNow();
    }

    private void ToggleMainUi()
    {
        config.WindowVisible = !config.WindowVisible;
        mainWindow.IsOpen = config.WindowVisible;
        persistence.SaveNow();
    }

    private void ToggleConfigUi()
    {
        config.SettingsWindowVisible = !config.SettingsWindowVisible;
        settingsWindow.IsOpen = config.SettingsWindowVisible;
        persistence.SaveNow();
    }

    private void DrawUi()
    {
        windowSystem.Draw();
        if (config.WindowVisible != mainWindow.IsOpen || config.SettingsWindowVisible != settingsWindow.IsOpen)
        {
            config.WindowVisible = mainWindow.IsOpen;
            config.SettingsWindowVisible = settingsWindow.IsOpen;
            persistence.SaveNow();
        }
    }

    internal void DrawEmbedded()
    {
        if (settingsWindow.IsOpen)
            settingsWindow.Draw();
        else
            mainWindow.Draw();
    }

    internal bool CanNavigateBackEmbedded() => settingsWindow.IsOpen;

    internal bool NavigateBackEmbedded()
    {
        if (!settingsWindow.IsOpen)
            return false;
        settingsWindow.IsOpen = false;
        config.SettingsWindowVisible = false;
        persistence.SaveNow();
        return true;
    }

    internal IReadOnlyList<AirTablet.Services.ControlCenterWidget> GetControlCenterWidgets() =>
    [
        new(
            "raffle.tickets",
            "RaffleManager",
            "Raffle tickets",
            "Total chances currently entered in the active raffle.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new(raffle.TotalTickets.ToString("N0"), $"{raffle.ParticipantCount:N0} contestants")),
        new(
            "raffle.jackpot",
            "RaffleManager",
            "Raffle jackpot",
            "Current jackpot and projected winner payout.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new($"{raffle.Jackpot:N0} gil", $"Payout {raffle.WinnerPayout:N0} gil")),
    ];

    public void Dispose()
    {
        persistence.SaveNow();
        mainWindow.Dispose();
        sound.Dispose();
        logo.Dispose();
        if (!tabletHosted)
        {
            DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
            DalamudServices.CommandManager.RemoveHandler(CommandName);
            DalamudServices.CommandManager.RemoveHandler(SettingsCommandName);
        }
        windowSystem.RemoveAllWindows();
    }
}
