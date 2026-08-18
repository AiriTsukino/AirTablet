using AutoGreet.Services;
using AutoGreet.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AutoGreet;

internal sealed class Plugin : IDisposable
{
    private const string CommandName = "/autogreet";
    private const string SettingsCommandName = "/autogreetsettings";
    private readonly WindowSystem windowSystem = new("AutoGreet");
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly VenueService venues;
    private readonly DetectionService detection;
    private readonly GreetingService greetings;
    private readonly ChatCommandService chatCommands;
    private readonly SoundService sound;
    private readonly TargetingService targeting;
    private readonly EmoteResumeService emoteResume;
    private readonly PendingEmoteQueueService pendingEmotes;
    private readonly DiagnosticLogService logs;
    private readonly MacroEngine macroEngine;
    private readonly QueueService queue;
    private readonly VisitorService visitors;
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
        MigrateBaseConfigDefaults(config);
        persistence = new PersistenceService(config);
        venues = new VenueService(config, persistence);
        detection = new DetectionService(config, persistence);
        greetings = new GreetingService(venues);
        chatCommands = new ChatCommandService();
        sound = new SoundService(config);
        targeting = new TargetingService();
        emoteResume = new EmoteResumeService(config, chatCommands);
        logs = new DiagnosticLogService();
        pendingEmotes = new PendingEmoteQueueService(config, chatCommands, targeting, emoteResume, logs);
        macroEngine = new MacroEngine(config, greetings, chatCommands, targeting, pendingEmotes, logs);
        queue = new QueueService(config, venues, persistence, greetings, macroEngine, detection, emoteResume, pendingEmotes, logs);
        visitors = new VisitorService(venues, persistence, queue, detection, config, sound, logs);
        greetings.AttachVisitorService(visitors);

        detection.PlayerEntered += visitors.OnPlayerEntered;
        detection.PlayerDoorbellEntered += visitors.OnPlayerDoorbellEntered;
        detection.PlayerPresentOnArrival += visitors.OnPlayerPresentOnArrival;
        detection.PlayerLeft += visitors.OnPlayerLeft;
        detection.PlayerCustomRegionMacroEntered += visitors.OnPlayerCustomRegionMacroEntered;

        mainWindow = new MainWindow(config, venues, visitors, queue, detection, persistence, logs, OpenSettingsWindow) { IsOpen = config.WindowVisible };
        settingsWindow = new SettingsWindow(config, venues, visitors, persistence, detection, greetings, sound, emoteResume, macroEngine) { IsOpen = config.SettingsWindowVisible };
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
            DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle AutoGreet window."
            });
            DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand)
            {
                HelpMessage = "Toggle AutoGreet settings window."
            });
            DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        }
        persistence.SaveNow();
    }


    private static void MigrateBaseConfigDefaults(Configuration config)
    {
        if (config.Version < 3)
        {
            if (Math.Abs(config.GreetingStartDelaySeconds - 1.0f) < 0.001f)
                config.GreetingStartDelaySeconds = 3.0f;
            if (Math.Abs(config.QueueDelaySeconds - 1.0f) < 0.001f)
                config.QueueDelaySeconds = 3.0f;
            config.Version = 3;
        }

        if (config.Version < 4)
            config.Version = 4;
    }

    private void OnCommand(string command, string arguments)
    {
        var mode = arguments.Trim();
        if (mode.Equals("probe", StringComparison.OrdinalIgnoreCase))
            return;
        if (mode.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            OpenMainWindow();
            return;
        }
        ToggleMainUi();
    }

    private void OnSettingsCommand(string command, string arguments)
    {
        var mode = arguments.Trim();
        if (mode.Equals("probe", StringComparison.OrdinalIgnoreCase))
            return;
        if (mode.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            OpenSettingsWindow();
            return;
        }
        ToggleConfigUi();
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
        // Do not force IsOpen from config every frame. Reassigning window state during
        // every draw can keep AutoGreet at the top of the ImGui z-order in some Dalamud
        // plugin draw-order situations. Commands/buttons update IsOpen directly; here we
        // only draw and then sync config from the actual close/open state.
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
            "autogreet.enabled",
            "AutoGreet",
            "Auto-greet",
            "Turn AutoGreet on or off for the active venue.",
            AirTablet.Services.ControlCenterWidgetKind.Toggle,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () =>
            {
                var activeVenue = venues.ActiveVenueOrNull;
                return activeVenue is null
                    ? new("No venue", "Select an active venue", config.AutoGreetEnabled, false)
                    : new(config.AutoGreetEnabled ? "On" : "Off", activeVenue.Name, config.AutoGreetEnabled);
            },
            enabled =>
            {
                config.AutoGreetEnabled = enabled;
                persistence.SaveNow();
                if (enabled)
                    queue.EnqueueEligibleUngreeted(true);
            }),
        new(
            "autogreet.visitors",
            "AutoGreet",
            "Current visitors",
            "People currently detected in the active venue area.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new(
                detection.PresentKeys.Count.ToString("N0"),
                detection.IsScanningActive ? "currently detected" : "detection inactive",
                null,
                detection.IsScanningActive)),
        new(
            "autogreet.queue",
            "AutoGreet",
            "Greeting queue",
            "Visitors waiting for their greeting macro.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new(queue.Entries.Count.ToString("N0"), "waiting to greet")),
    ];

    public void Dispose()
    {
        persistence.SaveNow();
        detection.PlayerEntered -= visitors.OnPlayerEntered;
        detection.PlayerDoorbellEntered -= visitors.OnPlayerDoorbellEntered;
        detection.PlayerPresentOnArrival -= visitors.OnPlayerPresentOnArrival;
        detection.PlayerLeft -= visitors.OnPlayerLeft;
        detection.PlayerCustomRegionMacroEntered -= visitors.OnPlayerCustomRegionMacroEntered;
        if (!tabletHosted)
        {
            DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi;
            DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
            DalamudServices.CommandManager.RemoveHandler(CommandName);
            DalamudServices.CommandManager.RemoveHandler(SettingsCommandName);
        }
        windowSystem.RemoveAllWindows();
        queue.Dispose();
        pendingEmotes.Dispose();
        greetings.Dispose();
        sound.Dispose();
        detection.Dispose();
        persistence.Dispose();
    }
}
