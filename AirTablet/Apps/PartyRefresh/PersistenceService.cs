using System.Text.Json;

namespace PartyRefresh;

internal sealed class PersistenceService
{
    private readonly Configuration config;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly List<VenueProfile> profiles = [];

    public PersistenceService(Configuration config)
    {
        this.config = config;
        config.Normalize();
        ProfilesDirectory = Path.Combine(DalamudServices.PluginInterface.ConfigDirectory.FullName, "Profiles");
        Load();
    }

    public string ProfilesDirectory { get; }
    public IReadOnlyList<VenueProfile> Profiles => profiles;
    public VenueProfile ActiveProfile => profiles.First(candidate =>
        candidate.Id.ToString("N").Equals(config.ActiveVenueProfileId, StringComparison.OrdinalIgnoreCase));

    public void ActivateProfile(Guid id)
    {
        if (profiles.All(profile => profile.Id != id))
            return;
        config.ActiveVenueProfileId = id.ToString("N");
        SaveConfig();
    }

    public VenueProfile AddProfile(string name, bool copyCurrent)
    {
        VenueProfile profile;
        if (copyCurrent)
        {
            profile = JsonSerializer.Deserialize<VenueProfile>(JsonSerializer.Serialize(ActiveProfile, json), json)
                ?? new VenueProfile();
            profile.Id = Guid.NewGuid();
        }
        else
        {
            profile = new VenueProfile();
        }
        profile.Name = UniqueProfileName(PartyFinderPreset.CleanName(name, "New Venue"));
        foreach (var preset in profile.Presets)
            preset.Id = Guid.NewGuid();
        profile.ActivePresetId = profile.Presets[0].Id;
        profile.Normalize();
        profiles.Add(profile);
        ActivateProfile(profile.Id);
        SaveProfile(profile);
        return profile;
    }

    public bool DeleteProfile(Guid id)
    {
        if (profiles.Count <= 1)
            return false;
        var profile = profiles.FirstOrDefault(candidate => candidate.Id == id);
        if (profile is null)
            return false;
        profiles.Remove(profile);
        var path = ProfilePath(profile.Id);
        if (File.Exists(path))
            File.Delete(path);
        ActivateProfile(profiles[0].Id);
        return true;
    }

    public PartyFinderPreset AddPreset(string name, bool copyCurrent)
    {
        var profile = ActiveProfile;
        var preset = copyCurrent
            ? profile.ActivePreset.Clone(name)
            : new PartyFinderPreset { Name = PartyFinderPreset.CleanName(name, "New Preset") };
        preset.Name = UniquePresetName(profile, preset.Name);
        preset.Normalize();
        profile.Presets.Add(preset);
        profile.ActivePresetId = preset.Id;
        SaveProfile(profile);
        return preset;
    }

    public bool DeletePreset(Guid id)
    {
        var profile = ActiveProfile;
        if (profile.Presets.Count <= 1)
            return false;
        var preset = profile.Presets.FirstOrDefault(candidate => candidate.Id == id);
        if (preset is null)
            return false;
        profile.Presets.Remove(preset);
        profile.ActivePresetId = profile.Presets[0].Id;
        SaveProfile(profile);
        return true;
    }

    public void ActivatePreset(Guid id)
    {
        var profile = ActiveProfile;
        if (profile.Presets.All(candidate => candidate.Id != id))
            return;
        profile.ActivePresetId = id;
        SaveProfile(profile);
    }

    public void SaveProfile(VenueProfile profile)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        profile.Normalize();
        File.WriteAllText(ProfilePath(profile.Id), JsonSerializer.Serialize(profile, json));
    }

    public void SaveConfig()
    {
        config.Normalize();
        DalamudServices.PluginInterface.SavePluginConfig(config);
    }

    public void ExportProfile(VenueProfile profile, string path)
    {
        profile.Normalize();
        File.WriteAllText(path, JsonSerializer.Serialize(profile, json));
    }

    public VenueProfile ImportProfile(string path)
    {
        var profile = JsonSerializer.Deserialize<VenueProfile>(File.ReadAllText(path), json)
            ?? throw new InvalidDataException("The selected file does not contain a PartyRefresh venue profile.");
        profile.Id = Guid.NewGuid();
        profile.Name = UniqueProfileName(PartyFinderPreset.CleanName(profile.Name, "Imported Venue"));
        profile.Presets ??= [];
        foreach (var preset in profile.Presets)
            preset.Id = Guid.NewGuid();
        profile.Normalize();
        profile.ActivePresetId = profile.Presets[0].Id;
        profiles.Add(profile);
        ActivateProfile(profile.Id);
        SaveProfile(profile);
        return profile;
    }

    private void Load()
    {
        Directory.CreateDirectory(ProfilesDirectory);
        foreach (var path in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<VenueProfile>(File.ReadAllText(path), json);
                if (profile is null)
                    continue;
                profile.Normalize();
                if (profiles.All(candidate => candidate.Id != profile.Id))
                    profiles.Add(profile);
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, "PartyRefresh skipped unreadable venue profile {Path}.", path);
            }
        }
        if (profiles.Count == 0)
        {
            var defaultProfile = new VenueProfile();
            defaultProfile.Normalize();
            profiles.Add(defaultProfile);
            SaveProfile(defaultProfile);
        }
        if (!Guid.TryParse(config.ActiveVenueProfileId, out var activeId) ||
            profiles.All(profile => profile.Id != activeId))
        {
            config.ActiveVenueProfileId = profiles[0].Id.ToString("N");
        }
        SaveConfig();
    }

    private string UniqueProfileName(string requested)
    {
        var name = requested;
        for (var suffix = 2; profiles.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{requested} {suffix}";
        return name;
    }

    private static string UniquePresetName(VenueProfile profile, string requested)
    {
        var name = requested;
        for (var suffix = 2; profile.Presets.Any(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{requested} {suffix}";
        return name;
    }

    private string ProfilePath(Guid id) => Path.Combine(ProfilesDirectory, id.ToString("N") + ".json");
}
