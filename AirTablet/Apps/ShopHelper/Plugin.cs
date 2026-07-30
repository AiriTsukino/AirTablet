using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ShopHelper.Services;
using ShopHelper.UI;

namespace ShopHelper;

internal sealed class Plugin : IDisposable
{
    private const string CommandName = "/shophelper";
    private const string SettingsCommandName = "/shophelpersettings";
    private readonly WindowSystem windowSystem = new("ShopHelper");
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly ShopService shopService;
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly bool tabletHosted;
    private bool mainWindowOpenedByShop;
    private bool foregroundOpenRequested;
    private static readonly string[] AutoOpenShopAddonNames =
    [
        "Shop",
        "ShopExchangeItem",
        "ShopExchangeCurrency",
        "InclusionShop",
        "FreeShop",
        "GrandCompanyExchange",
    ];

    public Plugin(IDalamudPluginInterface pluginInterface) : this(pluginInterface, false)
    {
    }

    internal Plugin(IDalamudPluginInterface pluginInterface, bool tabletHosted)
    {
        this.tabletHosted = tabletHosted;
        DalamudServices.Initialize(pluginInterface);
        config = DalamudServices.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Clamp();
        persistence = new PersistenceService(config);
        shopService = new ShopService(config);

        mainWindow = new MainWindow(config, persistence, shopService, OpenSettingsWindow) { IsOpen = config.WindowVisible };
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
            DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = "Toggle ShopHelper window." });
            DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand) { HelpMessage = "Toggle ShopHelper settings window." });
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        }

        foreach (var addonName in AutoOpenShopAddonNames)
            DalamudServices.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, OnShopAddonSetup);

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

    private void OnShopAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (!config.AutoOpenWithShop)
            return;

        mainWindowOpenedByShop = true;
        config.WindowVisible = true;
        mainWindow.IsOpen = true;
        if (tabletHosted)
        {
            foregroundOpenRequested = true;
            settingsWindow.IsOpen = false;
            config.SettingsWindowVisible = false;
        }
        persistence.SaveNow();
    }

    private void OpenSettingsWindow()
    {
        config.SettingsWindowVisible = true;
        settingsWindow.IsOpen = true;
        persistence.SaveNow();
    }

    private void OpenMainWindow()
    {
        mainWindowOpenedByShop = false;
        config.WindowVisible = true;
        mainWindow.IsOpen = true;
        persistence.SaveNow();
    }

    private void ToggleMainUi()
    {
        mainWindowOpenedByShop = false;
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
        if (mainWindowOpenedByShop && mainWindow.IsOpen && !shopService.IsShopOpen)
        {
            mainWindowOpenedByShop = false;
            mainWindow.IsOpen = false;
            config.WindowVisible = false;
            persistence.SaveNow();
        }

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

    internal void TickEmbedded()
    {
        if (!mainWindowOpenedByShop || !mainWindow.IsOpen || shopService.IsShopOpen)
            return;
        mainWindowOpenedByShop = false;
        mainWindow.IsOpen = false;
        config.WindowVisible = false;
        persistence.SaveNow();
    }

    internal bool ConsumeForegroundRequestEmbedded()
    {
        if (!foregroundOpenRequested)
            return false;

        foregroundOpenRequested = false;
        settingsWindow.IsOpen = false;
        mainWindow.IsOpen = true;
        config.SettingsWindowVisible = false;
        config.WindowVisible = true;
        return true;
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

    public void Dispose()
    {
        persistence.SaveNow();
        foreach (var addonName in AutoOpenShopAddonNames)
            DalamudServices.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, OnShopAddonSetup);

        if (!tabletHosted)
        {
            DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
            DalamudServices.CommandManager.RemoveHandler(CommandName);
            DalamudServices.CommandManager.RemoveHandler(SettingsCommandName);
        }
        windowSystem.RemoveAllWindows();
        shopService.Dispose();
        persistence.Dispose();
    }
}
