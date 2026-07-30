using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Newtonsoft.Json;
using ShiftKeeper.Services;
using ShiftKeeper.UI;

namespace ShiftKeeper;

internal sealed class Plugin : IDisposable
{
    private const string CommandName = "/shiftkeeper";
    private const string SettingsCommandName = "/shiftkeepersettings";
    private readonly WindowSystem windowSystem = new("ShiftKeeper");
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly StaffTrackingService tracking;
    private readonly TradePaymentService tradePayments;
    private readonly FileDialogService dialogs;
    private readonly TellWindow tellWindow;
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
        config = LoadConfiguration();
        if (config.Version < 5) config.Version = 5;
        persistence = new PersistenceService(config);
        tracking = new StaffTrackingService(config, persistence);
        tradePayments = new TradePaymentService(config, persistence);
        var chat = new ChatCommandService();
        var targeting = new TargetingService();
        dialogs = new FileDialogService();
        tellWindow = new TellWindow(chat);
        mainWindow = new MainWindow(config, persistence, tradePayments, targeting, dialogs, tellWindow.OpenFor, OpenSettingsWindow) { IsOpen = config.WindowVisible };
        settingsWindow = new SettingsWindow(mainWindow) { IsOpen = config.SettingsWindowVisible };
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(tellWindow);

        if (tabletHosted)
        {
            mainWindow.IsOpen = true;
            settingsWindow.IsOpen = false;
            config.WindowVisible = true;
            config.SettingsWindowVisible = false;
        }
        else
        {
            var info = new CommandInfo(OnCommand) { HelpMessage = "Toggle the ShiftKeeper dashboard." };
            DalamudServices.CommandManager.AddHandler(CommandName, info);
            DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand) { HelpMessage = "Toggle ShiftKeeper settings." });
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsUi;
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
        else ToggleSettingsUi();
    }

    private static Configuration LoadConfiguration()
    {
        if (DalamudServices.PluginInterface.GetPluginConfig() is Configuration current) return current;

        var configurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "pluginConfigs",
            "ShiftKeeper.json");
        try
        {
            if (File.Exists(configurationPath))
                return JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(configurationPath)) ?? new Configuration();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "ShiftKeeper could not load its configuration file.");
        }

        return new Configuration();
    }

    private void ToggleMainUi()
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
        config.WindowVisible = mainWindow.IsOpen;
        persistence.SaveNow();
    }

    private void OpenMainWindow()
    {
        mainWindow.IsOpen = true;
        config.WindowVisible = true;
        persistence.SaveNow();
    }

    private void OpenSettingsWindow()
    {
        settingsWindow.IsOpen = true;
        config.SettingsWindowVisible = true;
        persistence.SaveNow();
    }

    private void ToggleSettingsUi()
    {
        settingsWindow.IsOpen = !settingsWindow.IsOpen;
        config.SettingsWindowVisible = settingsWindow.IsOpen;
        persistence.SaveNow();
    }

    private void DrawUi()
    {
        windowSystem.Draw();
        dialogs.Pump();
        if (config.WindowVisible != mainWindow.IsOpen || config.SettingsWindowVisible != settingsWindow.IsOpen)
        {
            config.WindowVisible = mainWindow.IsOpen;
            config.SettingsWindowVisible = settingsWindow.IsOpen;
            persistence.SaveNow();
        }
    }

    internal void DrawEmbedded()
    {
        dialogs.Pump();
        if (settingsWindow.IsOpen)
            settingsWindow.Draw();
        else
            mainWindow.Draw();
        tellWindow.DrawEmbeddedPopup();
    }

    internal bool CanNavigateBackEmbedded() =>
        settingsWindow.IsOpen;

    internal bool NavigateBackEmbedded()
    {
        if (!settingsWindow.IsOpen)
            return false;
        settingsWindow.IsOpen = false;
        config.SettingsWindowVisible = false;
        persistence.SaveNow();
        return true;
    }

    public void Dispose()
    {
        persistence.SaveNow();
        if (!tabletHosted)
        {
            DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsUi;
            DalamudServices.CommandManager.RemoveHandler(CommandName);
            DalamudServices.CommandManager.RemoveHandler(SettingsCommandName);
        }
        windowSystem.RemoveAllWindows();
        dialogs.Dispose();
        tellWindow.Dispose();
        tradePayments.Dispose();
        tracking.Dispose();
        persistence.Dispose();
    }
}
