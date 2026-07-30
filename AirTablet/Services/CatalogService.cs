using System.Text.Json;
using AirTablet.Models;

namespace AirTablet.Services;

internal sealed class CatalogService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Configuration config;

    public CatalogService(Configuration config)
    {
        this.config = config;
        Apps = LoadBundled();
    }

    public IReadOnlyList<AppDescriptor> Apps { get; private set; }
    public string Status { get; private set; } = "Using bundled catalog.";
    public bool IsRefreshing { get; private set; }

    public async Task RefreshAsync()
    {
        if (IsRefreshing || string.IsNullOrWhiteSpace(config.CatalogUrl))
            return;

        IsRefreshing = true;
        try
        {
            var json = await http.GetStringAsync(config.CatalogUrl);
            var remote = ParseCatalog(json);
            if (remote is null || remote.Count == 0)
                throw new InvalidDataException("The remote catalog did not contain any apps.");

            Apps = MergeWithBundled(remote.Where(IsValid));
            Status = $"Catalog refreshed at {DateTime.Now:t}.";
        }
        catch (Exception ex)
        {
            Status = "Remote catalog unavailable; using the last loaded catalog.";
            DalamudServices.Log.Debug(ex, "AirTablet catalog refresh failed.");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void ReloadBundled()
    {
        Apps = LoadBundled();
        Status = "Bundled catalog restored.";
    }

    public void Dispose() => http.Dispose();

    private static IReadOnlyList<AppDescriptor> LoadBundled()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName
                ?? throw new DirectoryNotFoundException("Dalamud did not provide the AirTablet installation directory.");
            var candidates = new[]
            {
                Path.Combine(directory, "Resources", "apps.json"),
                Path.Combine(directory, "apps.json"),
            };
            var path = candidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException(
                    $"AirTablet could not find its bundled app catalog under '{directory}'.",
                    candidates[0]);
            var json = File.ReadAllText(path);
            return ParseCatalog(json).Where(IsValid).ToList();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "AirTablet could not load its bundled app catalog.");
            return [];
        }
    }

    private static bool IsValid(AppDescriptor app) =>
        !string.IsNullOrWhiteSpace(app.Id) &&
        !string.IsNullOrWhiteSpace(app.Name);

    private static IReadOnlyList<AppDescriptor> MergeWithBundled(
        IEnumerable<AppDescriptor> remoteApps)
    {
        var remote = remoteApps
            .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merged = new List<AppDescriptor>();

        foreach (var bundled in LoadBundled())
        {
            if (!remote.Remove(bundled.Id, out var current))
            {
                merged.Add(bundled);
                continue;
            }

            // Hub metadata can update independently, while the compiled app keeps its
            // packaged icon so the Home screen never depends on network access.
            current.IconUrl = bundled.IconUrl;
            merged.Add(current);
        }

        // Newly listed hub plugins remain visible in the catalog. They become runnable
        // once their source is deliberately added to AppHostService in a future build.
        merged.AddRange(remote.Values.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase));
        return merged;
    }

    private static List<AppDescriptor> ParseCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0)
            return [];

        var first = document.RootElement[0];
        if (!first.TryGetProperty("InternalName", out _))
            return JsonSerializer.Deserialize<List<AppDescriptor>>(json, JsonOptions) ?? [];

        var plugins = JsonSerializer.Deserialize<List<HubPluginEntry>>(json, JsonOptions) ?? [];
        return plugins
            .Where(plugin =>
                !plugin.IsHide &&
                !string.IsNullOrWhiteSpace(plugin.InternalName) &&
                !plugin.InternalName.Equals("AirTablet", StringComparison.OrdinalIgnoreCase))
            .Select(FromHub)
            .ToList();
    }

    private static AppDescriptor FromHub(HubPluginEntry plugin)
    {
        var internalName = plugin.InternalName.Trim();
        var repository = ParseRepository(plugin.RepoUrl);
        var mainCommand = string.IsNullOrWhiteSpace(plugin.AirTabletCommand)
            ? $"/{internalName.ToLowerInvariant()}"
            : NormalizeCommand(plugin.AirTabletCommand);
        var settingsCommand = string.IsNullOrWhiteSpace(plugin.AirTabletSettingsCommand)
            ? $"{mainCommand}settings"
            : NormalizeCommand(plugin.AirTabletSettingsCommand);

        return new AppDescriptor
        {
            Id = internalName,
            Name = string.IsNullOrWhiteSpace(plugin.Name) ? internalName : plugin.Name,
            Version = plugin.AssemblyVersion,
            Tagline = string.IsNullOrWhiteSpace(plugin.Punchline) ? plugin.Description : plugin.Punchline,
            Command = mainCommand,
            SettingsCommand = settingsCommand,
            Repository = repository,
            ManifestUrl = string.IsNullOrWhiteSpace(repository)
                ? string.Empty
                : $"https://raw.githubusercontent.com/{repository}/main/pluginmaster.json",
            IconUrl = plugin.IconUrl,
        };
    }

    private static string ParseRepository(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return uri.AbsolutePath.Trim('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCommand(string command) =>
        command.StartsWith('/') ? command : $"/{command}";

    private sealed class HubPluginEntry
    {
        public string Name { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string AssemblyVersion { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Punchline { get; set; } = string.Empty;
        public string RepoUrl { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public bool IsHide { get; set; }
        public string AirTabletCommand { get; set; } = string.Empty;
        public string AirTabletSettingsCommand { get; set; } = string.Empty;
    }
}
