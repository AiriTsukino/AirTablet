using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AirTablet.Services;

internal sealed class AppHostService : IDisposable
{
    private sealed record AppDefinition(
        string Id,
        Type ConfigurationType,
        Func<IAirTabletApp> Create,
        bool SupportsOriginalConfigMigration = true);

    private static readonly IReadOnlyList<AppDefinition> Definitions =
    [
        new("AutoGreet", typeof(AutoGreet.Configuration), () => new AutoGreet.AirTabletModule()),
        new("BarManager", typeof(BarManager.Configuration), () => new BarManager.AirTabletModule()),
        new("GambaAssistant", typeof(GambaAssistant.Configuration), () => new GambaAssistant.AirTabletModule()),
        new("MacroDeck", typeof(MacroDeck.Configuration), () => new MacroDeck.AirTabletModule(), false),
        new("RaffleManager", typeof(RaffleManager.Configuration), () => new RaffleManager.AirTabletModule()),
        new("ShiftKeeper", typeof(ShiftKeeper.Configuration), () => new ShiftKeeper.AirTabletModule()),
        new("ShopHelper", typeof(ShopHelper.Configuration), () => new ShopHelper.AirTabletModule()),
        new("ShoutRunner", typeof(ShoutRunner.Configuration), () => new ShoutRunner.AirTabletModule(), false),
    ];

    public static IReadOnlyList<string> BundledAppIds { get; } =
        Definitions.Select(definition => definition.Id).ToArray();

    private readonly Configuration config;
    private readonly Dictionary<string, IAirTabletApp> running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> errors = new(StringComparer.OrdinalIgnoreCase);

    public AppHostService(Configuration config)
    {
        this.config = config;
        config.DisabledApps ??= [];
        config.EnabledApps ??= [];

        foreach (var definition in Definitions)
        {
            if (IsEnabled(definition.Id))
                Start(definition);
        }
    }

    public IReadOnlyCollection<string> AvailableAppIds =>
        Definitions.Select(definition => definition.Id).ToArray();

    public string Status => errors.Count == 0
        ? $"{running.Count} of {Definitions.Count} bundled apps running."
        : $"{running.Count} apps running; {errors.Count} failed.";

    public string ConfigSourceDirectory => ResolveConfigSourceDirectory();

    public bool KeepTabletVisibleDuringTravel => running.Values.Any(app =>
    {
        try { return app.KeepTabletVisibleDuringTravel; }
        catch { return false; }
    });

    public bool IsAvailable(string id) =>
        Definitions.Any(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(string id) =>
        config.AppSelectionInitialized &&
        config.EnabledApps.Any(enabled =>
            enabled.Equals(id, StringComparison.OrdinalIgnoreCase));

    public bool IsRunning(string id) => running.ContainsKey(id);

    public string? GetError(string id) =>
        errors.TryGetValue(id, out var message) ? message : null;

    public bool CanNavigateBack(string id)
    {
        if (!running.TryGetValue(id, out var app))
            return false;

        try
        {
            return app.CanNavigateBack();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AirTablet app {App} could not report its navigation state.", id);
            return false;
        }
    }

    public bool Retry(string id)
    {
        var definition = Definitions.FirstOrDefault(candidate =>
            candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !IsEnabled(id))
            return false;

        Stop(definition.Id);
        errors.Remove(definition.Id);
        return Start(definition);
    }

    public bool SetEnabled(string id, bool enabled)
    {
        var definition = Definitions.FirstOrDefault(candidate =>
            candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return false;

        config.DisabledApps.RemoveAll(disabled =>
            disabled.Equals(id, StringComparison.OrdinalIgnoreCase));
        config.EnabledApps.RemoveAll(current =>
            current.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (enabled)
        {
            if (Start(definition))
            {
                config.AppSelectionInitialized = true;
                config.EnabledApps.Add(definition.Id);
                return true;
            }
            config.DisabledApps.Add(definition.Id);
            return false;
        }

        config.DisabledApps.Add(definition.Id);
        Stop(definition.Id);
        errors.Remove(definition.Id);
        return true;
    }

    public void TickAll()
    {
        foreach (var pair in running.ToArray())
        {
            try
            {
                pair.Value.Tick();
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(
                    ex,
                    "AirTablet app {App} failed during its background tick.",
                    pair.Key);
            }
        }
    }

    public string? ConsumeForegroundRequest()
    {
        foreach (var pair in running.ToArray())
        {
            try
            {
                if (pair.Value.ConsumeForegroundRequest())
                    return pair.Key;
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(
                    ex,
                    "AirTablet app {App} could not report its foreground request.",
                    pair.Key);
            }
        }

        return null;
    }

    public bool ConsumeHomeRequest()
    {
        foreach (var pair in running.ToArray())
        {
            try
            {
                if (pair.Value.ConsumeHomeRequest())
                    return true;
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(
                    ex,
                    "AirTablet app {App} could not report its home request.",
                    pair.Key);
            }
        }

        return false;
    }

    public bool Draw(string id)
    {
        if (!running.TryGetValue(id, out var app))
            return false;

        try
        {
            app.Draw();
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "AirTablet app {App} failed while drawing.", id);
            return false;
        }
    }

    public IReadOnlyList<ControlCenterWidget> GetControlCenterWidgets()
    {
        var widgets = new List<ControlCenterWidget>();
        foreach (var pair in running.ToArray())
        {
            try
            {
                widgets.AddRange(pair.Value.GetControlCenterWidgets());
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(
                    ex,
                    "AirTablet app {App} could not provide Control Center widgets.",
                    pair.Key);
            }
        }

        return widgets;
    }

    public bool NavigateBack(string id)
    {
        if (!running.TryGetValue(id, out var app))
            return false;

        try
        {
            return app.NavigateBack();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AirTablet app {App} could not navigate back.", id);
            return false;
        }
    }

    public ConfigMigrationResult MigrateOriginalConfigs()
    {
        var sourceRoot = ResolveConfigSourceDirectory();
        var tabletRoot = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        var appsRoot = Path.Combine(tabletRoot, "Apps");
        var backupRoot = Path.Combine(
            tabletRoot,
            "MigrationBackups",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var imported = new List<string>();
        var missing = new List<string>();
        var failed = new List<string>();

        foreach (var id in running.Keys.Reverse().ToArray())
            Stop(id);

        foreach (var definition in Definitions.Where(definition => definition.SupportsOriginalConfigMigration))
        {
            var targetDirectory = Path.Combine(
                appsRoot,
                definition.Id);
            var backupDirectory = Path.Combine(
                backupRoot,
                definition.Id);
            var backedUp = false;
            try
            {
                var sourceConfig = Path.Combine(sourceRoot, definition.Id + ".json");
                var sourceDirectory = Path.Combine(sourceRoot, definition.Id);
                var hasConfig = File.Exists(sourceConfig);
                var hasDirectory = Directory.Exists(sourceDirectory) &&
                    Directory.EnumerateFileSystemEntries(sourceDirectory).Any();
                if (!hasConfig && !hasDirectory)
                {
                    missing.Add(definition.Id);
                    continue;
                }

                if (Directory.Exists(targetDirectory) &&
                    Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                {
                    CopyDirectory(
                        targetDirectory,
                        backupDirectory,
                        overwrite: true);
                    backedUp = true;
                }

                RecreateAppDirectory(appsRoot, targetDirectory);
                if (hasDirectory)
                    CopyDirectory(sourceDirectory, targetDirectory, overwrite: true);
                if (hasConfig)
                    File.Copy(sourceConfig, Path.Combine(targetDirectory, "config.json"), true);
                LocalizeExternalDataDirectories(
                    definition.Id,
                    sourceConfig,
                    targetDirectory);
                if (backedUp &&
                    definition.Id.Equals(
                        "AutoGreet",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var merge =
                        AutoGreetMigrationMerger.Merge(
                            backupDirectory,
                            targetDirectory);
                    if (merge.VipAssignments > 0 ||
                        merge.BlacklistEntries > 0 ||
                        merge.VipTiers > 0)
                    {
                        DalamudServices.Log.Information(
                            "AirTablet preserved {VipCount} AutoGreet VIP assignments, " +
                            "{BlacklistCount} blacklist entries, and {TierCount} VIP tiers " +
                            "while importing original settings.",
                            merge.VipAssignments,
                            merge.BlacklistEntries,
                            merge.VipTiers);
                    }
                }

                imported.Add(definition.Id);
            }
            catch (Exception ex)
            {
                if (backedUp)
                {
                    try
                    {
                        RecreateAppDirectory(
                            appsRoot,
                            targetDirectory);
                        CopyDirectory(
                            backupDirectory,
                            targetDirectory,
                            overwrite: true);
                    }
                    catch (Exception restoreException)
                    {
                        DalamudServices.Log.Error(
                            restoreException,
                            "AirTablet could not restore the backup for {App} after migration failed.",
                            definition.Id);
                    }
                }

                failed.Add(definition.Id);
                DalamudServices.Log.Warning(
                    ex,
                    "AirTablet could not migrate the original {App} configuration.",
                    definition.Id);
            }
        }

        foreach (var definition in Definitions)
        {
            if (IsEnabled(definition.Id))
                Start(definition);
        }

        return new ConfigMigrationResult(
            imported,
            missing,
            failed,
            backupRoot,
            sourceRoot);
    }

    public void Dispose()
    {
        foreach (var id in running.Keys.Reverse().ToArray())
            Stop(id);
        errors.Clear();
    }

    private bool Start(AppDefinition definition)
    {
        if (running.ContainsKey(definition.Id))
            return true;

        IAirTabletApp? app = null;
        try
        {
            app = definition.Create();
            var configDirectory = new DirectoryInfo(Path.Combine(
                DalamudServices.PluginInterface.ConfigDirectory.FullName,
                "Apps",
                definition.Id));
            var scopedInterface = ScopedPluginInterfaceProxy.CreateScoped(
                DalamudServices.PluginInterface,
                definition.Id,
                configDirectory,
                definition.ConfigurationType);

            app.Initialize(scopedInterface);
            running.Add(definition.Id, app);
            errors.Remove(definition.Id);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                app?.Dispose();
            }
            catch (Exception disposeException)
            {
                DalamudServices.Log.Warning(
                    disposeException,
                    "AirTablet app {App} also failed while cleaning up after startup.",
                    definition.Id);
            }

            errors[definition.Id] = ex.Message;
            DalamudServices.Log.Error(
                ex,
                "AirTablet could not start bundled app {App}.",
                definition.Id);
            return false;
        }
    }

    private string ResolveConfigSourceDirectory()
    {
        var configured = Environment.ExpandEnvironmentVariables(
            config.PluginConfigSourceDirectory?.Trim() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                return Path.GetFullPath(configured);
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return configured;
            }
        }

        return Configuration.DefaultPluginConfigSourceDirectory;
    }

    private void Stop(string id)
    {
        if (!running.Remove(id, out var app))
            return;

        try
        {
            app.Dispose();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AirTablet app {App} failed to dispose.", id);
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private static void LocalizeExternalDataDirectories(
        string appId,
        string sourceConfig,
        string targetDirectory)
    {
        if (!File.Exists(sourceConfig))
            return;

        var config = JObject.Parse(File.ReadAllText(sourceConfig));
        var changed = false;

        if (appId.Equals("BarManager", StringComparison.OrdinalIgnoreCase))
        {
            changed |= CopyConfiguredDirectory(
                config,
                "DataDirectory",
                Path.Combine(targetDirectory, "BarManagerData"));
            changed |= CopyConfiguredDirectory(
                config,
                "AuditReportDirectory",
                Path.Combine(targetDirectory, "BarManagerData", "AuditReports"));
            changed |= CopyConfiguredDirectory(
                config,
                "GambaSettingsDirectory",
                Path.Combine(targetDirectory, "BarManagerData", "GambaSettings"));
        }
        else if (appId.Equals("RaffleManager", StringComparison.OrdinalIgnoreCase))
        {
            changed |= CopyConfiguredDirectory(
                config,
                "DataDirectory",
                targetDirectory);
        }

        if (!changed)
            return;

        var targetConfig = Path.Combine(targetDirectory, "config.json");
        File.WriteAllText(targetConfig, config.ToString(Formatting.Indented));
    }

    private static bool CopyConfiguredDirectory(
        JObject config,
        string propertyName,
        string destination)
    {
        var property = config.Property(propertyName, StringComparison.OrdinalIgnoreCase);
        var configuredPath = property?.Value.Type == JTokenType.String
            ? property.Value.Value<string>()
            : null;
        if (string.IsNullOrWhiteSpace(configuredPath))
            return false;

        var source = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(configuredPath));
        if (Directory.Exists(source))
            CopyExternalDirectory(source, destination);

        property!.Value = string.Empty;
        return true;
    }

    private static void CopyExternalDirectory(string source, string destination)
    {
        var normalizedSource = Path.GetFullPath(source)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDestination = Path.GetFullPath(destination)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalizedSource.Equals(
                normalizedDestination,
                StringComparison.OrdinalIgnoreCase))
            return;

        var sourcePrefix = normalizedSource + Path.DirectorySeparatorChar;
        if (normalizedDestination.StartsWith(
                sourcePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to copy an external app data directory into itself.");
        }

        CopyDirectory(normalizedSource, normalizedDestination, overwrite: true);
    }

    private static void RecreateAppDirectory(string appsRoot, string targetDirectory)
    {
        var root = Path.GetFullPath(appsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(targetDirectory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to replace an app directory outside AirTablet.");

        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
    }
}

internal sealed record ConfigMigrationResult(
    IReadOnlyList<string> Imported,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Failed,
    string BackupDirectory,
    string SourceDirectory)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                Imported.Count == 0
                    ? "No original plugin configurations were found."
                    : $"Imported {Imported.Count}: {string.Join(", ", Imported)}.",
            };
            if (Missing.Count > 0)
                parts.Add($"Not found: {string.Join(", ", Missing)}.");
            if (Failed.Count > 0)
                parts.Add($"Failed: {string.Join(", ", Failed)}.");
            if (Imported.Count > 0)
                parts.Add("The originals were left unchanged and the previous tablet copies were backed up.");
            parts.Add($"Source: {SourceDirectory}");
            return string.Join(" ", parts);
        }
    }
}
