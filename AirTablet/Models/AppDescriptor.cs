namespace AirTablet.Models;

public sealed class AppDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string SettingsCommand { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
}
