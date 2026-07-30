using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Games.DeathRoll;
using GambaAssistant.Services;
using GambaAssistant.UI;
using Dalamud.Plugin;

namespace GambaAssistant;

internal sealed class Plugin : IDisposable
{
    private const string CommandName = "/gambaassistant";
    private const string SettingsCommandName = "/gambaassistantsettings";
    private readonly WindowSystem windowSystem = new("GambaAssistant");
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly LogService log;
    private readonly PersistenceService persistence;
    private readonly ProfileService profiles;
    private readonly ChatQueueService chatQueue;
    private readonly PartyService party;
    private readonly PlayerSessionService players;
    private readonly DealerLedgerService ledger;
    private readonly TradeMonitorService tradeMonitor;
    private readonly DiceService dice;
    private readonly ChatMonitorService chatMonitor;
    private readonly OverlayService overlays;
    private readonly UndoService undo;
    private readonly DemoModeService demo;
    private readonly ExportService exports;
    private readonly DeathRollTournamentService deathRoll;
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private DeathRollBracketWindow? drtBracketWindow;
    private DateTimeOffset nextBlackjackAutosaveAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextDeathRollAutosaveAt = DateTimeOffset.MinValue;
    private bool disposed;
    private readonly bool tabletHosted;

    public Plugin(IDalamudPluginInterface pluginInterface) : this(pluginInterface, false)
    {
    }

    internal Plugin(IDalamudPluginInterface pluginInterface, bool tabletHosted)
    {
        this.tabletHosted = tabletHosted;
        try
        {
            DalamudServices.Initialize(pluginInterface);
            config = DalamudServices.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            log = new LogService();
            persistence = new PersistenceService(config);
            profiles = new ProfileService(config, persistence, log);
            session = persistence.LoadBlackjackSession() ?? new BlackjackSession();
            if (session.Rules is null)
                session.Rules = profiles.ActiveProfile.BlackjackRules;
            chatQueue = new ChatQueueService(config, log);
            party = new PartyService(log);
            players = new PlayerSessionService(session, log);
            ledger = new DealerLedgerService(session, log);
            tradeMonitor = new TradeMonitorService(config, session, ledger, log);
            chatQueue.SetActionCommandDelayPredicate(() => tradeMonitor.IsTradeWindowLikelyOpen);
            dice = new DiceService(session, chatQueue, log);
            chatMonitor = new ChatMonitorService(dice, tradeMonitor, log);
            overlays = new OverlayService(config, session, log);
            undo = new UndoService(config, log);
            demo = new DemoModeService(session, chatQueue, log);
            exports = new ExportService(persistence, config, session, log);
            var restoredDeathRollTournament = persistence.LoadDeathRollTournament();
            deathRoll = new DeathRollTournamentService(config, party, chatQueue, log, restoredDeathRollTournament);

            mainWindow = new MainWindow(config, session, profiles, party, players, ledger, tradeMonitor, dice, chatQueue, overlays, undo, demo, exports, deathRoll, log, OpenSettingsWindow, SetDrtBracketWindowOpen) { IsOpen = config.WindowVisible };
            settingsWindow = new SettingsWindow(config, session, profiles, persistence, log) { IsOpen = config.SettingsWindowVisible };
            drtBracketWindow = new DeathRollBracketWindow(mainWindow) { IsOpen = config.DeathRoll.BracketWindowOpen };
            windowSystem.AddWindow(mainWindow);
            windowSystem.AddWindow(settingsWindow);
            windowSystem.AddWindow(drtBracketWindow);

            if (tabletHosted)
            {
                mainWindow.IsOpen = true;
                settingsWindow.IsOpen = false;
                drtBracketWindow.IsOpen = false;
                config.WindowVisible = true;
                config.SettingsWindowVisible = false;
                config.DeathRoll.BracketWindowOpen = false;
            }
            else
            {
                DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = "Toggle GambaAssistant table window." });
                DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand) { HelpMessage = "Toggle GambaAssistant settings window." });
                DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
                DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
                DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
            }

            SafeRun("Save initial plugin config", () => persistence.SaveNow());
            SafeRun("Save initial Blackjack session", () => persistence.SaveBlackjackSession(session));
            SafeRun("Save initial DRT session", () => persistence.SaveDeathRollTournament(deathRoll.Tournament));
        }
        catch (Exception ex)
        {
            LogException("GambaAssistant startup", ex);
            Dispose();
            throw;
        }
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
        SafeRun("Save main window visibility", () => persistence.SaveNow());
    }

    private void OpenSettingsWindow()
    {
        config.SettingsWindowVisible = true;
        settingsWindow.IsOpen = true;
        SafeRun("Save settings window visibility", () => persistence.SaveNow());
    }

    private void SetDrtBracketWindowOpen(bool isOpen)
    {
        config.DeathRoll.BracketWindowOpen = isOpen;
        if (drtBracketWindow is not null)
            drtBracketWindow.IsOpen = isOpen;
        SafeRun("Save DRT bracket window visibility", () => persistence.SaveNow());
    }

    private void ToggleMainUi()
    {
        config.WindowVisible = !config.WindowVisible;
        mainWindow.IsOpen = config.WindowVisible;
        SafeRun("Save main window visibility", () => persistence.SaveNow());
    }

    private void ToggleConfigUi()
    {
        config.SettingsWindowVisible = !config.SettingsWindowVisible;
        settingsWindow.IsOpen = config.SettingsWindowVisible;
        SafeRun("Save settings window visibility", () => persistence.SaveNow());
    }

    private void DrawUi()
    {
        SafeRun("Poll trade window", () => tradeMonitor.Tick());
        SafeRun("Draw windows", () => windowSystem.Draw());
        SafeRun("Draw overlays", () => overlays.Draw());

        var saveVisibility = false;
        if (config.WindowVisible != mainWindow.IsOpen || config.SettingsWindowVisible != settingsWindow.IsOpen)
        {
            config.WindowVisible = mainWindow.IsOpen;
            config.SettingsWindowVisible = settingsWindow.IsOpen;
            saveVisibility = true;
        }

        if (drtBracketWindow is not null && config.DeathRoll.BracketWindowOpen != drtBracketWindow.IsOpen)
        {
            config.DeathRoll.BracketWindowOpen = drtBracketWindow.IsOpen;
            saveVisibility = true;
        }

        if (saveVisibility)
            SafeRun("Save window visibility", () => persistence.SaveNow());

        SafeRun("Autosave Blackjack session", AutosaveBlackjackSession);
        SafeRun("Autosave DRT session", AutosaveDeathRollTournament);
    }

    internal void DrawEmbedded()
    {
        SafeRun("Draw embedded GambaAssistant", () =>
        {
            if (drtBracketWindow is { IsOpen: true })
                drtBracketWindow.Draw();
            else if (settingsWindow.IsOpen)
                settingsWindow.Draw();
            else
                mainWindow.Draw();
        });
    }

    internal void TickEmbedded()
    {
        SafeRun("Poll trade window", () => tradeMonitor.Tick());
        SafeRun("Draw overlays", () => overlays.Draw());
        SafeRun("Autosave Blackjack session", AutosaveBlackjackSession);
        SafeRun("Autosave DRT session", AutosaveDeathRollTournament);
    }

    internal bool CanNavigateBackEmbedded() =>
        drtBracketWindow is { IsOpen: true } || settingsWindow.IsOpen;

    internal bool NavigateBackEmbedded()
    {
        if (drtBracketWindow is { IsOpen: true })
        {
            drtBracketWindow.IsOpen = false;
            config.DeathRoll.BracketWindowOpen = false;
            SafeRun("Save embedded bracket visibility", () => persistence.SaveNow());
            return true;
        }
        if (!settingsWindow.IsOpen)
            return false;
        settingsWindow.IsOpen = false;
        config.SettingsWindowVisible = false;
        SafeRun("Save embedded settings visibility", () => persistence.SaveNow());
        return true;
    }

    private void AutosaveBlackjackSession()
    {
        if (!config.General.BlackjackSessionAutosaveEnabled)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < nextBlackjackAutosaveAt)
            return;

        nextBlackjackAutosaveAt = now.AddSeconds(1);
        persistence.SaveBlackjackSession(session);
    }

    private void AutosaveDeathRollTournament()
    {
        if (!config.General.DeathRollSessionAutosaveEnabled)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < nextDeathRollAutosaveAt)
            return;

        nextDeathRollAutosaveAt = now.AddSeconds(1);
        persistence.SaveDeathRollTournament(deathRoll.Tournament);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        if (!tabletHosted)
        {
            SafeRun("Unsubscribe main window draw", () => { DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi; });
            SafeRun("Unsubscribe main window command", () => { DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi; });
            SafeRun("Unsubscribe settings window command", () => { DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi; });
            SafeRun("Remove main command", () => DalamudServices.CommandManager.RemoveHandler(CommandName));
            SafeRun("Remove settings command", () => DalamudServices.CommandManager.RemoveHandler(SettingsCommandName));
        }
        SafeRun("Remove windows", () => windowSystem.RemoveAllWindows());

        SafeRun("Dispose DRT service", () => { if (deathRoll is not null) deathRoll.Dispose(); });
        SafeRun("Dispose chat monitor", () => { if (chatMonitor is not null) chatMonitor.Dispose(); });
        SafeRun("Dispose dice service", () => { if (dice is not null) dice.Dispose(); });
        SafeRun("Dispose chat queue", () => { if (chatQueue is not null) chatQueue.Dispose(); });

        SafeRun("Save Blackjack session on unload", () => { if (persistence is not null && session is not null) persistence.SaveBlackjackSession(session); });
        SafeRun("Save DRT session on unload", () => { if (persistence is not null && deathRoll is not null) persistence.SaveDeathRollTournament(deathRoll.Tournament); });
        SafeRun("Save plugin config on unload", () => { if (persistence is not null) persistence.SaveNow(); });
        SafeRun("Dispose persistence", () => { if (persistence is not null) persistence.Dispose(); });
    }

    private void SafeRun(string actionName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogException(actionName, ex);
        }
    }

    private void LogException(string actionName, Exception ex)
    {
        try
        {
            log?.Add(LogCategory.Errors, $"{actionName} failed: {ex.Message}");
        }
        catch
        {
            // ignored
        }

        try
        {
            DalamudServices.Log.Error(ex, $"{actionName} failed.");
        }
        catch
        {
            // ignored
        }
    }
}
