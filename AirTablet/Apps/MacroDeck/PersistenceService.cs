using Newtonsoft.Json;
using Dalamud.Plugin;

namespace MacroDeck;

internal sealed class PersistenceService
{
    private readonly Configuration config;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly string path;
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        TypeNameHandling = TypeNameHandling.None,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };

    public List<VenueProfile> Venues { get; private set; } = [];

    public PersistenceService(Configuration config, IDalamudPluginInterface pluginInterface)
    {
        this.config = config;
        this.pluginInterface = pluginInterface;
        path = Path.Combine(pluginInterface.ConfigDirectory.FullName, "MacroDeckProfiles.json");
        Load();
    }

    public VenueProfile ActiveVenue
    {
        get
        {
            EnsureDefaults();
            var venue = Venues.FirstOrDefault(candidate => candidate.Id == config.ActiveVenueId) ?? Venues[0];
            config.ActiveVenueId = venue.Id;
            return venue;
        }
    }

    public VenueProfile AddVenue(string name)
    {
        var venue = VenueProfile.Create(name);
        Venues.Add(venue);
        config.ActiveVenueId = venue.Id;
        SaveNow();
        return venue;
    }

    public bool DeleteVenue(Guid id)
    {
        if (Venues.Count <= 1 || Venues.RemoveAll(venue => venue.Id == id) == 0)
            return false;
        config.ActiveVenueId = Venues[0].Id;
        SaveNow();
        return true;
    }

    public void SaveNow()
    {
        EnsureDefaults();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(new ProfileStore { Venues = Venues }, JsonSettings));
        pluginInterface.SavePluginConfig(config);
    }

    public void ExportVenue(VenueProfile venue, string exportPath)
    {
        var export = new VenueExportFile { Venue = venue };
        File.WriteAllText(exportPath, JsonConvert.SerializeObject(export, JsonSettings));
    }

    public VenueProfile ImportVenue(string importPath)
    {
        var export = JsonConvert.DeserializeObject<VenueExportFile>(File.ReadAllText(importPath), JsonSettings)
            ?? throw new InvalidDataException("This file is not a MacroDeck venue profile.");
        var venue = export.Venue ?? throw new InvalidDataException("The file did not contain a venue profile.");
        venue.Id = Guid.NewGuid();
        venue.Name = string.IsNullOrWhiteSpace(venue.Name) ? "Imported Venue" : venue.Name.Trim() + " (Imported)";
        venue.Buttons ??= [];
        RegenerateEntryIds(venue.Buttons);
        venue.ControlCenterSlots = [null, null, null, null];
        venue.ControlCenterPads = new(StringComparer.OrdinalIgnoreCase);
        NormalizeVenue(venue);
        Venues.Add(venue);
        config.ActiveVenueId = venue.Id;
        SaveNow();
        return venue;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(path))
                Venues = JsonConvert.DeserializeObject<ProfileStore>(File.ReadAllText(path), JsonSettings)?.Venues ?? [];
        }
        catch (Exception ex)
        {
            AirTablet.DalamudServices.Log.Warning(ex, "MacroDeck could not load its venue profiles.");
            Venues = [];
        }
        EnsureDefaults();
    }

    private void EnsureDefaults()
    {
        if (Venues.Count == 0)
            Venues.Add(VenueProfile.Create("Default Venue"));
        foreach (var venue in Venues)
            NormalizeVenue(venue);
        if (config.ActiveVenueId == Guid.Empty || Venues.All(venue => venue.Id != config.ActiveVenueId))
            config.ActiveVenueId = Venues[0].Id;
    }

    private static void NormalizeVenue(VenueProfile venue)
    {
        venue.Name = string.IsNullOrWhiteSpace(venue.Name) ? "Venue" : venue.Name.Trim();
        venue.Buttons ??= [];
        NormalizeEntries(venue.Buttons, false);
        venue.ControlCenterSlots ??= [];
        venue.ControlCenterSlots = venue.ControlCenterSlots.Take(4).Concat(Enumerable.Repeat<Guid?>(null, 4)).Take(4).ToList();
        venue.ControlCenterPads = new Dictionary<string, List<Guid?>>(venue.ControlCenterPads ?? [], StringComparer.OrdinalIgnoreCase);
        if (!venue.ControlCenterPads.ContainsKey("macrodeck.pad") && venue.ControlCenterSlots.Any(id => id is not null))
            venue.ControlCenterPads["macrodeck.pad"] = venue.ControlCenterSlots.ToList();
        foreach (var key in venue.ControlCenterPads.Keys.ToArray())
            venue.ControlCenterPads[key] = venue.ControlCenterPads[key].Take(4).Concat(Enumerable.Repeat<Guid?>(null, 4)).Take(4).ToList();
        var assignedMacros = new HashSet<Guid>();
        foreach (var slots in venue.ControlCenterPads.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Value))
        {
            for (var index = 0; index < slots.Count; index++)
            {
                if (slots[index] is { } id && !assignedMacros.Add(id))
                    slots[index] = null;
            }
        }
    }

    private static void NormalizeEntries(List<DeckEntry> entries, bool reserveNavigationKey)
    {
        var firstSlot = reserveNavigationKey ? 1 : 0;
        var capacity = reserveNavigationKey ? 31 : 32;
        if (entries.Count > capacity)
            entries.RemoveRange(capacity, entries.Count - capacity);
        var usedSlots = new HashSet<int>();
        foreach (var entry in entries)
        {
            entry.Slot = Math.Clamp(entry.Slot, firstSlot, 31);
            if (usedSlots.Contains(entry.Slot))
                entry.Slot = Enumerable.Range(firstSlot, capacity).First(slot => !usedSlots.Contains(slot));
            usedSlots.Add(entry.Slot);
            if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
            entry.Title = DeckEntry.NormalizeTitle(entry.Title, entry.Kind == DeckEntryKind.Folder ? "Folder" : "Macro");
            entry.ImagePath ??= string.Empty;
            entry.Message ??= string.Empty;
            entry.EmoteCommand ??= string.Empty;
            entry.Script ??= string.Empty;
            if (entry.Kind == DeckEntryKind.Macro && string.IsNullOrWhiteSpace(entry.Script))
            {
                var prefix = entry.Channel.ToString().ToLowerInvariant();
                var legacyLines = entry.Message.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => $"/{prefix} {line}")
                    .ToList();
                if (!string.IsNullOrWhiteSpace(entry.EmoteCommand)) legacyLines.Add(entry.EmoteCommand.Trim());
                entry.Script = string.Join('\n', legacyLines);
            }
            entry.Children ??= [];
            NormalizeEntries(entry.Children, true);
        }
    }

    private static void RegenerateEntryIds(IEnumerable<DeckEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.Id = Guid.NewGuid();
            entry.Children ??= [];
            RegenerateEntryIds(entry.Children);
        }
    }
}
