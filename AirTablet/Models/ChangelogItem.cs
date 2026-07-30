namespace AirTablet.Models;

public sealed record ChangelogItem(
    string PluginName,
    string Version,
    DateTimeOffset Date,
    IReadOnlyList<string> Changes);
