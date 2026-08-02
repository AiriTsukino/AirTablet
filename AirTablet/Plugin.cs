using AirTablet.Services;
using AirTablet.UI;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace AirTablet;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly TimeSpan InitialWorldReadyDelay = TimeSpan.FromSeconds(1.25);
    private const string MainCommand = "/airtablet";
    private const string SettingsCommand = "/airtabletsettings";
    private const string RecoveryCommand = "/airtabletrecovery";
    private const string AppStateFileName = "app-state.json";
    private sealed class AppSelectionState
    {
        public int Version { get; set; } = 2;
        public bool Initialized { get; set; }
        public List<string> EnabledApps { get; set; } = [];
    }

    private readonly Configuration config;
    private readonly CatalogService catalog;
    private readonly ChangelogService changelog;
    private readonly TextureCache textures;
    private readonly FileDialogService dialogs;
    private readonly AppHostService appHost;
    private readonly TabletWindow window;
    private DateTime nextSaveAt = DateTime.MinValue;
    private DateTime? worldReadySince;
    private bool initialWorldReady;
    private bool savePending;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = LoadConfiguration(out var hadExistingConfig);
        var previousVersion = config.Version;
        RestoreAppSelectionState(config);
        if (hadExistingConfig && previousVersion < 10)
            MigrateAppSelection(config);
        NormalizeAppSelection(config);
        if (hadExistingConfig && previousVersion < 13)
        {
            // Existing users already made their app choices before the welcome
            // screen existed, so upgrades must never be treated as fresh installs.
            if (previousVersion < 7)
                config.SetupCompleted = true;
            if (previousVersion < 10)
                config.TabletSize = "Large";
            // The control tutorial is part of fresh setup. Existing installations
            // can replay it by running the welcome setup from General.
            config.TutorialCompleted = true;
            config.AppOrder ??= [];
            config.Version = 13;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        if (hadExistingConfig && previousVersion < 14)
        {
            config.LastReadChangelogVersion ??= string.Empty;
            config.Version = 14;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        if (hadExistingConfig && previousVersion < 15)
        {
            config.ShowAirTabOsTooltips = true;
            config.Version = 15;
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        SaveAppSelectionState(config);
        catalog = new CatalogService(config);
        changelog = new ChangelogService();
        textures = new TextureCache();
        dialogs = new FileDialogService();
        appHost = new AppHostService(config);
        window = new TabletWindow(
            config,
            catalog,
            changelog,
            textures,
            dialogs,
            appHost,
            QueueSave,
            SaveNow);

        DalamudServices.CommandManager.AddHandler(MainCommand, new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open or close AirTablet.",
        });
        DalamudServices.CommandManager.AddHandler(SettingsCommand, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Open AirTablet directly to its settings app.",
        });
        DalamudServices.CommandManager.AddHandler(RecoveryCommand, new CommandInfo(OnRecoveryCommand)
        {
            HelpMessage = "Recover AirTablet to the center of the active game screen.",
        });
        DalamudServices.PluginInterface.UiBuilder.Draw += Draw;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi += Toggle;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += window.OpenSettings;

        _ = RefreshRemoteDataAsync();
    }

    public void Dispose()
    {
        SaveNow();
        DalamudServices.PluginInterface.UiBuilder.Draw -= Draw;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= Toggle;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= window.OpenSettings;
        DalamudServices.CommandManager.RemoveHandler(MainCommand);
        DalamudServices.CommandManager.RemoveHandler(SettingsCommand);
        DalamudServices.CommandManager.RemoveHandler(RecoveryCommand);
        appHost.Dispose();
        dialogs.Dispose();
        textures.Dispose();
        changelog.Dispose();
        catalog.Dispose();
    }

    private void OnMainCommand(string command, string arguments)
        => Toggle();

    private void OnSettingsCommand(string command, string arguments) => window.OpenSettings();

    private void OnRecoveryCommand(string command, string arguments) =>
        window.RequestRecovery();

    private void Toggle()
    {
        if (config.WindowVisible)
        {
            config.WindowVisible = false;
            QueueSave();
        }
        else
        {
            window.OpenHome();
        }
    }

    private void Draw()
    {
        try
        {
            var gameReady = UpdateInitialWorldReady();
            var keepTravelShellVisible = appHost.KeepTabletVisibleDuringTravel;
            if (gameReady || keepTravelShellVisible)
                appHost.TickAll();
            if (gameReady || keepTravelShellVisible || config.ShowBeforeCharacterLogin)
                window.Draw(keepTravelShellVisible || config.ShowBeforeCharacterLogin);
            if (savePending && DateTime.UtcNow >= nextSaveAt)
                SaveNow();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "AirTablet draw failed.");
        }
    }

    private bool UpdateInitialWorldReady()
    {
        if (!DalamudServices.ClientState.IsLoggedIn)
        {
            initialWorldReady = false;
            worldReadySince = null;
            return false;
        }

        if (initialWorldReady)
            return true;

        var localPlayer = DalamudServices.ObjectTable.LocalPlayer;
        var ready = DalamudServices.PlayerState.IsLoaded &&
                    DalamudServices.ClientState.TerritoryType != 0 &&
                    localPlayer is { IsTargetable: true };
        if (!ready)
        {
            worldReadySince = null;
            return false;
        }

        worldReadySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - worldReadySince.Value < InitialWorldReadyDelay)
            return false;

        initialWorldReady = true;
        return true;
    }

    private async Task RefreshRemoteDataAsync()
    {
        await catalog.RefreshAsync();
        await changelog.RefreshAsync(catalog.Apps);
    }

    private void QueueSave()
    {
        savePending = true;
        nextSaveAt = DateTime.UtcNow.AddMilliseconds(350);
    }

    private void SaveNow()
    {
        NormalizeAppSelection(config);
        DalamudServices.PluginInterface.SavePluginConfig(config);
        SaveAppSelectionState(config);
        savePending = false;
    }

    private static Configuration LoadConfiguration(out bool hadExistingConfig)
    {
        // Use Dalamud's normal configuration loader first. The direct file path is
        // only a recovery fallback for unusual hot-reload timing.
        try
        {
            if (DalamudServices.PluginInterface.GetPluginConfig() is Configuration loaded)
            {
                hadExistingConfig = true;
                return loaded;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(
                ex,
                "AirTablet could not load its configuration through Dalamud.");
        }

        try
        {
            var path = DalamudServices.PluginInterface.ConfigFile.FullName;
            if (File.Exists(path))
            {
                var fallback = JsonConvert.DeserializeObject<Configuration>(
                    File.ReadAllText(path));
                if (fallback is not null)
                {
                    DalamudServices.Log.Warning(
                        "AirTablet recovered its configuration directly from {ConfigPath}.",
                        path);
                    hadExistingConfig = true;
                    return fallback;
                }
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(
                ex,
                "AirTablet could not recover its configuration from disk.");
        }

        hadExistingConfig = false;
        return new Configuration();
    }

    private static void RestoreAppSelectionState(Configuration target)
    {
        try
        {
            var path = AppStatePath();
            if (!File.Exists(path))
                return;

            var state = JsonConvert.DeserializeObject<AppSelectionState>(
                File.ReadAllText(path));
            if (state is { Version: >= 2 })
            {
                target.AppSelectionInitialized = state.Initialized;
                target.EnabledApps = state.EnabledApps ?? [];
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(
                ex,
                "AirTablet could not restore its durable app selection state.");
        }
    }

    private static void SaveAppSelectionState(Configuration source)
    {
        try
        {
            var path = AppStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new AppSelectionState
            {
                Initialized = source.AppSelectionInitialized,
                EnabledApps = source.EnabledApps.ToList(),
            };
            File.WriteAllText(
                path,
                JsonConvert.SerializeObject(state, Formatting.Indented));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(
                ex,
                "AirTablet could not save its durable app selection state.");
        }
    }

    private static void NormalizeAppSelection(Configuration target)
    {
        target.EnabledApps = (target.EnabledApps ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var enabled = target.EnabledApps.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var unknownDisabled = (target.DisabledApps ?? [])
            .Where(id => !AppHostService.BundledAppIds.Contains(
                id,
                StringComparer.OrdinalIgnoreCase));
        target.DisabledApps = unknownDisabled
            .Concat(AppHostService.BundledAppIds.Where(id =>
                !target.AppSelectionInitialized || !enabled.Contains(id)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void MigrateAppSelection(Configuration target)
    {
        if (target.AppSelectionInitialized || !target.SetupCompleted)
            return;

        var disabled = (target.DisabledApps ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = AppHostService.BundledAppIds
            .Where(id => !disabled.Contains(id))
            .ToList();

        // Versions before 1.0.17 could accidentally persist the fresh-install
        // all-disabled defaults over a completed setup. Treat that impossible
        // completed state as corrupted and restore the apps for one-time selection.
        if (enabled.Count == 0)
            enabled = AppHostService.BundledAppIds.ToList();

        target.AppSelectionInitialized = true;
        target.EnabledApps = enabled;
    }

    private static string AppStatePath() =>
        Path.Combine(
            DalamudServices.PluginInterface.ConfigDirectory.FullName,
            AppStateFileName);
}
