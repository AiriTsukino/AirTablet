using System.Globalization;
using AirTablet.Models;

namespace AirTablet.Services;

internal sealed class ChangelogService : IDisposable
{
    private const string ChangelogRelativePath = @"Resources\CHANGELOG.txt";

    public IReadOnlyList<ChangelogItem> Items { get; private set; } = [];
    public bool IsRefreshing { get; private set; }
    public string Status { get; private set; } = "Loading bundled changelog...";

    public ChangelogService()
    {
        LoadBundled();
    }

    public Task RefreshAsync(IReadOnlyList<AppDescriptor> apps)
    {
        LoadBundled();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    private void LoadBundled()
    {
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        try
        {
            var path = Path.Combine(
                DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName
                    ?? string.Empty,
                ChangelogRelativePath);
            if (!File.Exists(path))
            {
                Items = [];
                Status = $"Bundled changelog was not found at {ChangelogRelativePath}.";
                return;
            }

            Items = Parse(File.ReadAllLines(path))
                .OrderByDescending(item => item.Date)
                .ThenBy(item => item.PluginName)
                .ToList();
            Status = Items.Count == 0
                ? "The bundled changelog has no entries yet."
                : $"Loaded {Items.Count} bundled update entr{(Items.Count == 1 ? "y" : "ies")}.";
        }
        catch (Exception ex)
        {
            Items = [];
            Status = "The bundled changelog could not be loaded.";
            DalamudServices.Log.Warning(ex, "AirTablet could not load its bundled changelog.");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private static IReadOnlyList<ChangelogItem> Parse(IEnumerable<string> lines)
    {
        var items = new List<ChangelogItem>();
        string? app = null;
        string? version = null;
        DateTimeOffset date = default;
        var changes = new List<string>();

        void Commit()
        {
            if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(version))
                return;

            items.Add(new ChangelogItem(
                app,
                version,
                date == default ? DateTimeOffset.Now : date,
                changes.ToList()));
            changes.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('['))
            {
                var closingBracket = line.IndexOf(']');
                var versionMarker = line.LastIndexOf(" v", StringComparison.OrdinalIgnoreCase);
                if (closingBracket > 1 && versionMarker > closingBracket)
                {
                    Commit();
                    var dateText = line[1..closingBracket].Trim();
                    date = DateTimeOffset.TryParseExact(
                        dateText,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out var parsedDate)
                        ? parsedDate
                        : DateTimeOffset.Now;
                    app = line[(closingBracket + 1)..versionMarker].Trim();
                    version = line[(versionMarker + 2)..].Trim();
                    continue;
                }
            }

            if ((line.StartsWith("- ") || line.StartsWith("* ")) && app is not null)
                changes.Add(line[2..].Trim());
        }

        Commit();
        return items;
    }
}
