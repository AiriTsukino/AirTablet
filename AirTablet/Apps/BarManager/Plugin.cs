using BarManager.Services;
using BarManager.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace BarManager;

internal sealed class Plugin : IDisposable
{
    private const string CommandName = "/barmanager";
    private const string SettingsCommandName = "/barmanagersettings";
    private readonly WindowSystem windowSystem = new("BarManager");
    private readonly Configuration config;
    private readonly PersistenceService persistence;
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
        persistence = new PersistenceService(config);
        mainWindow = new MainWindow(config, persistence, OpenSettingsWindow) { IsOpen = config.WindowVisible };
        settingsWindow = new SettingsWindow(config, persistence) { IsOpen = config.SettingsWindowVisible };
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
            DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = "Toggle BarManager main window." });
            DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand) { HelpMessage = "Toggle BarManager settings window." });
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
            "barmanager.sales",
            "BarManager",
            "Night sales",
            "Drink, buyout, and tip income in the current audit.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () =>
            {
                var venue = config.ActiveVenue;
                var audit = config.CurrentAudit;
                var total = ReportService.DrinkSales(venue, audit) + ReportService.BuyoutSales(venue, audit) + audit.Tips;
                var drinks = audit.DrinkSales.Sum(sale => Math.Max(0, sale.Count));
                return new($"{total:N0} gil", $"{drinks:N0} drinks  •  {audit.Tips:N0} gil tips");
            }),
        new(
            "barmanager.jackpot",
            "BarManager",
            "Bar jackpot",
            "Current gamba drink jackpot.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new($"{config.CurrentAudit.JackpotCurrent:N0}", "gil jackpot")),
    ];

    public void Dispose()
    {
        persistence.SaveNow();
        mainWindow.Dispose();
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
