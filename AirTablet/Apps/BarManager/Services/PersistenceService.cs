using System.Text.Json;
using BarManager.Models;

namespace BarManager.Services;

internal sealed class PersistenceService
{
    private readonly Configuration config;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public string ConfigRoot { get; }
    public string DataRoot => string.IsNullOrWhiteSpace(config.DataDirectory) ? Path.Combine(ConfigRoot, "BarManagerData") : config.DataDirectory;
    public string AuditReportRoot => string.IsNullOrWhiteSpace(config.AuditReportDirectory) ? Path.Combine(DataRoot, "AuditReports") : config.AuditReportDirectory;
    public string GambaSettingsRoot => string.IsNullOrWhiteSpace(config.GambaSettingsDirectory) ? Path.Combine(DataRoot, "GambaSettings") : config.GambaSettingsDirectory;
    public string GambaReportRoot => Path.Combine(AuditReportRoot, "GambaReports");
    private string DataFile => Path.Combine(DataRoot, "bar-data.json");

    public PersistenceService(Configuration config)
    {
        this.config = config;
        ConfigRoot = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(ConfigRoot);
        LoadData();
        EnsureFolders();
    }

    public void LoadData()
    {
        try
        {
            EnsureFolders();
            if (File.Exists(DataFile))
            {
                var data = JsonSerializer.Deserialize<BarManagerData>(File.ReadAllText(DataFile), jsonOptions);
                if (data is not null)
                    config.Data = data;
            }

            config.EnsureDefaults();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "BarManager failed to load data files.");
            config.Data = new BarManagerData();
            config.EnsureDefaults();
        }
    }

    public void SaveNow()
    {
        SaveConfig();
        SaveData();
    }

    public void SaveConfig()
    {
        try
        {
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "BarManager failed to save main configuration.");
        }
    }

    public void SaveData()
    {
        try
        {
            config.EnsureDefaults();
            EnsureFolders();
            File.WriteAllText(DataFile, JsonSerializer.Serialize(config.Data, jsonOptions));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "BarManager failed to save data files.");
        }
    }

    public string SaveAuditReport(string reportText, string? path = null)
    {
        EnsureFolders();
        path = string.IsNullOrWhiteSpace(path) ? GetDefaultAuditReportPath() : path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, reportText);
        return path;
    }

    public string SaveGambaReport(string reportText, string? path = null)
    {
        EnsureFolders();
        path = string.IsNullOrWhiteSpace(path) ? GetDefaultGambaReportPath() : path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, reportText);
        return path;
    }

    public string GetDefaultAuditReportPath()
    {
        var safeVenue = MakeSafeFileName(config.ActiveVenue.Name);
        return Path.Combine(AuditReportRoot, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{safeVenue}_audit.txt");
    }

    public string GetDefaultGambaReportPath()
    {
        var safeVenue = MakeSafeFileName(config.ActiveVenue.Name);
        return Path.Combine(GambaReportRoot, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{safeVenue}_gamba.txt");
    }

    public string GetDefaultGambaExportPath()
    {
        var safeVenue = MakeSafeFileName(config.ActiveVenue.Name);
        return Path.Combine(GambaSettingsRoot, $"{safeVenue}_gamba_{DateTime.Now:yyyyMMdd_HHmmss}.json");
    }

    public string GetDefaultVenueExportPath()
    {
        var safeVenue = MakeSafeFileName(config.ActiveVenue.Name);
        return Path.Combine(DataRoot, $"{safeVenue}_venue_profile_{DateTime.Now:yyyyMMdd_HHmmss}.json");
    }

    public string ExportGambaSettings(string? path = null)
    {
        EnsureFolders();
        var venue = config.ActiveVenue;
        var export = new GambaSettingsExport
        {
            VenueName = venue.Name,
            Gamba = venue.Gamba,
            ExportedAt = DateTime.Now,
        };
        path = string.IsNullOrWhiteSpace(path) ? GetDefaultGambaExportPath() : path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(export, jsonOptions));
        return path;
    }

    public string ExportVenueProfile(string? path = null)
    {
        EnsureFolders();
        var export = new VenueProfileExport
        {
            Venue = config.ActiveVenue,
            ExportedAt = DateTime.Now,
        };
        path = string.IsNullOrWhiteSpace(path) ? GetDefaultVenueExportPath() : path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(export, jsonOptions));
        return path;
    }

    public bool ImportGambaSettings(string path, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                message = "Import file was not found.";
                return false;
            }

            var json = File.ReadAllText(path);
            var export = JsonSerializer.Deserialize<GambaSettingsExport>(json, jsonOptions);
            var gamba = export?.Gamba ?? JsonSerializer.Deserialize<GambaSettings>(json, jsonOptions);
            if (gamba is null)
            {
                message = "Import file did not contain valid gamba settings.";
                return false;
            }

            config.ActiveVenue.Gamba = gamba;
            SaveNow();
            message = $"Imported gamba settings from {path}";
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "BarManager failed to import gamba settings.");
            message = $"Import failed: {ex.Message}";
            return false;
        }
    }

    public bool ImportVenueProfile(string path, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                message = "Import file was not found.";
                return false;
            }

            var json = File.ReadAllText(path);
            var export = JsonSerializer.Deserialize<VenueProfileExport>(json, jsonOptions);
            var venue = export?.Venue ?? JsonSerializer.Deserialize<VenueProfile>(json, jsonOptions);
            if (venue is null)
            {
                message = "Import file did not contain a valid venue profile.";
                return false;
            }

            venue.Id = Guid.NewGuid();
            venue.Name = GetUniqueVenueName(string.IsNullOrWhiteSpace(venue.Name) ? "Imported Venue" : venue.Name);
            foreach (var drink in venue.Drinks)
                drink.Id = Guid.NewGuid();

            config.Venues.Add(venue);
            config.ActiveVenueId = venue.Id;
            config.CurrentAudit = new BarAuditState { JackpotCurrent = venue.JackpotBase };
            SaveNow();
            message = $"Imported venue profile '{venue.Name}' from {path}";
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "BarManager failed to import venue profile.");
            message = $"Import failed: {ex.Message}";
            return false;
        }
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(AuditReportRoot);
        Directory.CreateDirectory(GambaSettingsRoot);
        Directory.CreateDirectory(GambaReportRoot);
    }

    private string GetUniqueVenueName(string baseName)
    {
        var name = baseName.Trim();
        if (!config.Venues.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            return name;

        var index = 2;
        while (config.Venues.Any(v => string.Equals(v.Name, $"{name} ({index})", StringComparison.OrdinalIgnoreCase)))
            index++;

        return $"{name} ({index})";
    }

    public static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((string.IsNullOrWhiteSpace(value) ? "Venue" : value).Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Trim();
    }
}
