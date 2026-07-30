using System.Text.Json;
using System.Text.Json.Nodes;
using RaffleManager.Models;

namespace RaffleManager.Services;

internal sealed class PersistenceService
{
    private readonly Configuration config;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public string ConfigRoot { get; }
    public string DataRoot => string.IsNullOrWhiteSpace(config.DataDirectory) ? Path.Combine(ConfigRoot, "RaffleManager") : config.DataDirectory;
    private string ProfilesFile => Path.Combine(DataRoot, "venue-profiles.json");

    public PersistenceService(Configuration config)
    {
        this.config = config;
        ConfigRoot = DalamudServices.PluginInterface.ConfigDirectory.Parent?.FullName
            ?? DalamudServices.PluginInterface.ConfigDirectory.FullName;
        LoadData();
    }

    public void LoadData()
    {
        try
        {
            EnsureFolders();
            if (File.Exists(ProfilesFile))
            {
                var store = JsonSerializer.Deserialize<VenueProfileStore>(File.ReadAllText(ProfilesFile), jsonOptions);
                if (store?.Profiles is not null)
                {
                    config.VenueProfiles = store.Profiles
                        .Where(p => p is not null)
                        .GroupBy(p => Configuration.SanitizeProfileName(p.Name), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
                }
            }

            config.EnsureDefaults();
            SaveData();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "RaffleManager failed to load venue profiles.");
            config.VenueProfiles = new Dictionary<string, VenueProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = new() { Name = "Default", VenueName = "Default" }
            };
            config.ActiveVenueProfile = "Default";
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
            config.EnsureDefaults();
            DalamudServices.PluginInterface.SavePluginConfig(config);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "RaffleManager failed to save configuration.");
        }
    }

    public void SaveData()
    {
        try
        {
            config.EnsureDefaults();
            EnsureFolders();
            var store = new VenueProfileStore { Profiles = config.VenueProfiles.Values.OrderBy(p => p.Name).ToList() };
            File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(store, jsonOptions));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "RaffleManager failed to save venue profiles.");
        }
    }

    public bool CreateProfile(string name, bool copyCurrent)
    {
        name = Configuration.SanitizeProfileName(name);
        if (config.VenueProfiles.ContainsKey(name)) return false;

        var source = config.Profile;
        config.VenueProfiles[name] = copyCurrent
            ? CloneProfile(source, name)
            : new VenueProfile { Name = name, VenueName = name };
        config.ActiveVenueProfile = name;
        SaveNow();
        return true;
    }


    public bool ExportProfile(string path, VenueProfile profile, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                message = "No export path was selected.";
                return false;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var export = CloneProfileWithData(profile, profile.Name);
            // Do not include local logo file paths in exported venue profiles.
            // Those paths are machine-specific and can break or leak local usernames when shared.
            var exportJson = JsonSerializer.SerializeToNode(export, jsonOptions);
            if (exportJson is JsonObject exportObject)
                exportObject.Remove(nameof(VenueProfile.CustomLogoPath));
            File.WriteAllText(path, exportJson?.ToJsonString(jsonOptions) ?? "{}");
            message = $"Exported profile '{profile.Name}' to {path}.";
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "RaffleManager failed to export venue profile.");
            message = $"Export failed: {ex.Message}";
            return false;
        }
    }

    public bool ImportProfile(string path, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                message = "Selected profile file was not found.";
                return false;
            }

            var json = File.ReadAllText(path);
            VenueProfile? imported;
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.TryGetProperty("Profiles", out _))
                {
                    var store = JsonSerializer.Deserialize<VenueProfileStore>(json, jsonOptions);
                    imported = store?.Profiles?.FirstOrDefault();
                }
                else
                {
                    imported = JsonSerializer.Deserialize<VenueProfile>(json, jsonOptions);
                }
            }

            if (imported is null)
            {
                message = "The selected file did not contain a valid RaffleManager venue profile.";
                return false;
            }

            imported.Name = Configuration.SanitizeProfileName(string.IsNullOrWhiteSpace(imported.Name)
                ? Path.GetFileNameWithoutExtension(path)
                : imported.Name);
            // Imported profiles should not carry over another user's local logo path.
            imported.CustomLogoPath = string.Empty;
            Configuration.EnsureProfileDefaults(imported);

            config.VenueProfiles[imported.Name] = imported;
            config.ActiveVenueProfile = imported.Name;
            SaveNow();
            message = $"Imported and activated profile '{imported.Name}'.";
            return true;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "RaffleManager failed to import venue profile.");
            message = $"Import failed: {ex.Message}";
            return false;
        }
    }

    public bool DeleteProfile(string name)
    {
        name = Configuration.SanitizeProfileName(name);
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return false;
        if (!config.VenueProfiles.Remove(name)) return false;
        if (config.ActiveVenueProfile.Equals(name, StringComparison.OrdinalIgnoreCase))
            config.ActiveVenueProfile = "Default";
        SaveNow();
        return true;
    }

    public void EnsureFolders() => Directory.CreateDirectory(DataRoot);

    private static VenueProfile CloneProfile(VenueProfile source, string newName)
    {
        var clone = CloneProfileWithData(source, newName);
        clone.VenueName = newName;
        clone.Data = new RaffleState();
        return clone;
    }

    private static VenueProfile CloneProfileWithData(VenueProfile source, string newName) => new()
    {
        Name = newName,
        VenueName = source.VenueName,
        CustomLogoPath = source.CustomLogoPath,
        TicketPrice = source.TicketPrice,
        BaseJackpot = source.BaseJackpot,
        WinnerSplitPercent = source.WinnerSplitPercent,
        BogoBonusTicketsCountTowardJackpot = source.BogoBonusTicketsCountTowardJackpot,
        AnnounceWinner = source.AnnounceWinner,
        AnnouncementChannel = source.AnnouncementChannel,
        WinnerMessageTemplate = source.WinnerMessageTemplate,
        TickSoundEnabled = source.TickSoundEnabled,
        TickVolume = source.TickVolume,
        MainWindowLeftPanelRatio = source.MainWindowLeftPanelRatio,
        Data = new RaffleState
        {
            Entries = source.Data.Entries.Select(e => new RaffleEntry
            {
                Id = e.Id,
                Name = e.Name,
                World = e.World,
                Tickets = e.Tickets,
                JackpotTickets = e.EffectiveJackpotTickets,
            }).ToList(),
            History = source.Data.History
                .Select(snapshot => snapshot.Select(e => new RaffleEntry
                {
                    Id = e.Id,
                    Name = e.Name,
                    World = e.World,
                    Tickets = e.Tickets,
                    JackpotTickets = e.EffectiveJackpotTickets,
                }).ToArray())
                .ToList(),
            WinnerHistory = source.Data.WinnerHistory
                .Select(w => new WinnerRecord
                {
                    Name = w.Name,
                    World = w.World,
                    Tickets = w.Tickets,
                    JackpotTickets = w.JackpotTickets,
                    TotalTickets = w.TotalTickets,
                    TotalJackpotTickets = w.TotalJackpotTickets,
                    TotalParticipants = w.TotalParticipants,
                    Jackpot = w.Jackpot,
                    Payout = w.Payout,
                    SplitPercent = w.SplitPercent,
                    PulledAt = w.PulledAt,
                })
                .ToList(),
        },
    };
}
