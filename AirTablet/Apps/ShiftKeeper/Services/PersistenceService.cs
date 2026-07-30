using Newtonsoft.Json;
using ShiftKeeper.Models;

namespace ShiftKeeper.Services;

public sealed class PersistenceService : IDisposable
{
    private const int StorageVersion = 5;
    private readonly Configuration config;
    private readonly object gate = new();
    private bool dirty;
    private DateTimeOffset lastSave = DateTimeOffset.MinValue;
    private bool disposed;

    public List<VenueProfile> Venues { get; private set; } = [];
    public string DataDirectory { get; }

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        TypeNameHandling = TypeNameHandling.None,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };

    public PersistenceService(Configuration config)
    {
        this.config = config;
        DataDirectory = DalamudServices.PluginInterface.ConfigDirectory.FullName;
        Load();
    }

    public VenueProfile ActiveVenue
    {
        get
        {
            EnsureDefaults();
            return Venues.FirstOrDefault(x => x.Id == config.ActiveVenueId) ?? Venues[0];
        }
    }

    public void MarkDirty() => dirty = true;

    public void SaveIfDue()
    {
        if (dirty && DateTimeOffset.UtcNow - lastSave >= TimeSpan.FromSeconds(10)) SaveNow();
    }

    public void SaveNow()
    {
        if (disposed) return;
        lock (gate)
        {
            try
            {
                EnsureDefaults();
                Directory.CreateDirectory(DataDirectory);
                SaveFile("VenueProfiles.json", new VenueProfilesFile
                {
                    ActiveVenueId = config.ActiveVenueId,
                    Venues = Venues.Select(v => new VenueRecord
                    {
                        Id = v.Id, Name = v.Name, CurrencyLabel = v.CurrencyLabel, TrackingDays = v.TrackingDays, Shifts = v.Shifts
                    }).ToList()
                });
                SaveFile("StaffLists.json", new StaffListsFile
                {
                    StaffByVenue = Venues.ToDictionary(v => v.Id.ToString(), v => v.Staff)
                });
                SaveFile("NightSessions.json", new NightSessionsFile
                {
                    CurrentByVenue = Venues.ToDictionary(v => v.Id.ToString(), v => v.CurrentNight),
                    HistoryByVenue = Venues.ToDictionary(v => v.Id.ToString(), v => v.NightHistory.TakeLast(30).ToList())
                });
                DalamudServices.PluginInterface.SavePluginConfig(config);
                dirty = false;
                lastSave = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Error(ex, "ShiftKeeper failed to save data.");
            }
        }
    }

    public VenueProfile AddVenue(string name)
    {
        var venue = VenueProfile.Create(string.IsNullOrWhiteSpace(name) ? "New Venue" : name.Trim());
        Venues.Add(venue);
        config.ActiveVenueId = venue.Id;
        SaveNow();
        return venue;
    }

    public bool DeleteVenue(Guid id)
    {
        if (Venues.Count <= 1) return false;
        var removed = Venues.RemoveAll(v => v.Id == id) > 0;
        if (removed)
        {
            config.ActiveVenueId = Venues[0].Id;
            SaveNow();
        }
        return removed;
    }

    public void ResetNight(VenueProfile venue)
    {
        venue.CurrentNight.EndedUtc = DateTimeOffset.UtcNow;
        venue.NightHistory.Add(venue.CurrentNight);
        venue.NightHistory = venue.NightHistory.TakeLast(30).ToList();
        venue.CurrentNight = new NightSession();
        foreach (var staff in venue.Staff.Where(x => x.Enabled)) venue.CurrentNight.GetOrCreate(staff.Id);
        SaveNow();
    }

    public string ExportVenue(VenueProfile venue, string path)
    {
        var export = new VenueExportFile { Venue = venue };
        File.WriteAllText(path, JsonConvert.SerializeObject(export, JsonSettings));
        return path;
    }

    public VenueProfile ImportVenue(string path)
    {
        var export = JsonConvert.DeserializeObject<VenueExportFile>(File.ReadAllText(path), JsonSettings)
                     ?? throw new InvalidDataException("This file is not a ShiftKeeper venue profile.");
        var venue = export.Venue ?? throw new InvalidDataException("The profile did not contain a venue.");
        venue.Id = Guid.NewGuid();
        venue.Name = string.IsNullOrWhiteSpace(venue.Name) ? "Imported Venue" : venue.Name.Trim() + " (Imported)";
        venue.Shifts ??= [];
        venue.Staff ??= [];
        venue.NormalizeTrackingDays();
        venue.NightHistory = [];
        venue.CurrentNight = new NightSession();
        if (venue.Shifts.Count == 0) venue.Shifts.Add(new ShiftDefinition());
        var shiftIds = venue.Shifts.Select(x => x.Id).ToHashSet();
        foreach (var member in venue.Staff)
        {
            member.Id = Guid.NewGuid();
            member.ShiftIds ??= [];
            member.ShiftIds = member.ShiftIds.Where(shiftIds.Contains).Distinct().ToList();
            if (member.ShiftIds.Count == 0 && shiftIds.Contains(member.ShiftId)) member.ShiftIds.Add(member.ShiftId);
            venue.NormalizeShiftAssignments(member);
            venue.CurrentNight.GetOrCreate(member.Id);
        }
        Venues.Add(venue);
        config.ActiveVenueId = venue.Id;
        SaveNow();
        return venue;
    }

    private void Load()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var profiles = LoadFile<VenueProfilesFile>("VenueProfiles.json");
            var staff = LoadFile<StaffListsFile>("StaffLists.json");
            var nights = LoadFile<NightSessionsFile>("NightSessions.json");
            if (profiles is not null)
            {
                config.ActiveVenueId = profiles.ActiveVenueId != Guid.Empty ? profiles.ActiveVenueId : config.ActiveVenueId;
                foreach (var source in profiles.Venues)
                {
                    var key = source.Id.ToString();
                    Venues.Add(new VenueProfile
                    {
                        Id = source.Id,
                        Name = source.Name,
                        CurrencyLabel = source.CurrencyLabel,
                        TrackingDays = source.TrackingDays ?? [.. VenueProfile.WeekDayOrder],
                        Shifts = source.Shifts ?? [],
                        Staff = staff?.StaffByVenue.GetValueOrDefault(key) ?? [],
                        CurrentNight = nights?.CurrentByVenue.GetValueOrDefault(key) ?? new NightSession(),
                        NightHistory = nights?.HistoryByVenue.GetValueOrDefault(key) ?? [],
                    });
                }
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "ShiftKeeper failed to load split data; a default venue will be used.");
            Venues = [];
        }
        EnsureDefaults();
        SaveNow();
    }

    private void EnsureDefaults()
    {
        if (Venues.Count == 0) Venues.Add(VenueProfile.Create("Default Venue"));
        foreach (var venue in Venues)
        {
            venue.Shifts ??= [];
            venue.Staff ??= [];
            venue.NormalizeTrackingDays();
            venue.NightHistory ??= [];
            venue.CurrentNight ??= new NightSession();
            if (venue.Shifts.Count == 0) venue.Shifts.Add(new ShiftDefinition());
            foreach (var member in venue.Staff)
            {
                if (member.Id == Guid.Empty) member.Id = Guid.NewGuid();
                venue.NormalizeShiftAssignments(member);
                MigratePaymentRecord(venue.CurrentNight.GetOrCreate(member.Id));
            }
            foreach (var night in venue.NightHistory)
                foreach (var record in night.Staff)
                    MigratePaymentRecord(record);
        }
        if (config.ActiveVenueId == Guid.Empty || Venues.All(v => v.Id != config.ActiveVenueId)) config.ActiveVenueId = Venues[0].Id;
    }

    private static void MigratePaymentRecord(NightlyStaffRecord record)
    {
        if (record.TotalPaidGil <= 0 && record.PaidAmount > 0)
            record.TotalPaidGil = (long)MathF.Round(record.PaidAmount, MidpointRounding.AwayFromZero);
    }

    private T? LoadFile<T>(string name) where T : class
    {
        var path = Path.Combine(DataDirectory, name);
        return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path), JsonSettings) : null;
    }

    private void SaveFile<T>(string name, T value)
    {
        var path = Path.Combine(DataDirectory, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(value, JsonSettings));
        File.Move(temp, path, true);
    }

    public void Dispose()
    {
        SaveNow();
        disposed = true;
    }

    private sealed class VenueProfilesFile
    {
        public int Version { get; set; } = StorageVersion;
        public Guid ActiveVenueId { get; set; }
        public List<VenueRecord> Venues { get; set; } = [];
    }
    private sealed class VenueRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "Venue";
        public string CurrencyLabel { get; set; } = "gil";
        public List<DayOfWeek> TrackingDays { get; set; } = [.. VenueProfile.WeekDayOrder];
        public List<ShiftDefinition> Shifts { get; set; } = [];
    }
    private sealed class StaffListsFile
    {
        public int Version { get; set; } = StorageVersion;
        public Dictionary<string, List<StaffMember>> StaffByVenue { get; set; } = [];
    }
    private sealed class NightSessionsFile
    {
        public int Version { get; set; } = StorageVersion;
        public Dictionary<string, NightSession> CurrentByVenue { get; set; } = [];
        public Dictionary<string, List<NightSession>> HistoryByVenue { get; set; } = [];
    }
}
