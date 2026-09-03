using AutoGreet.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AutoGreet.Services;

internal sealed class VenueExport
{
    public string Format { get; set; } = "AirTablet.AutoGreet.Venue";
    public int Version { get; set; } = 1;
    public VenueProfile? Venue { get; set; }
    public List<CustomDetectionRegion> Regions { get; set; } = [];
}

internal static class VenueTransfer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Formatting = Formatting.Indented,
        MaxDepth = 64,
        ContractResolver = new StoredPropertiesResolver(),
    };

    public static string Export(VenueProfile source, IEnumerable<GreetingProfile> allProfiles, IEnumerable<CustomDetectionRegion> regions)
    {
        var venue = Clone(source);
        ClearRuntimeData(venue);
        // Venues can select a greeting profile owned by another venue. Bundle
        // that dependency too so the export remains self-contained.
        if (venue.GreetingProfiles.All(profile => profile.Id != venue.ActiveGreetingProfileId))
        {
            var active = allProfiles.FirstOrDefault(profile => profile.Id == venue.ActiveGreetingProfileId);
            if (active is not null) venue.GreetingProfiles.Add(Clone(active));
        }
        var ids = RegionIds(venue).Where(id => id != Guid.Empty).ToHashSet();
        return JsonConvert.SerializeObject(new VenueExport
        {
            Venue = venue,
            Regions = regions.Where(region => ids.Contains(region.Id)).Select(Clone).ToList(),
        }, Settings);
    }

    public static VenueExport Import(string json, IEnumerable<string> existingNames)
    {
        if (json.Length > 10_000_000) throw new InvalidDataException("The venue profile is too large (maximum 10 MB).");
        var export = JsonConvert.DeserializeObject<VenueExport>(json, Settings)
            ?? throw new InvalidDataException("The file does not contain an AutoGreet venue profile.");
        // Require the explicit marker rather than accepting arbitrary JSON with
        // default DTO values as a valid import.
        var root = Newtonsoft.Json.Linq.JObject.Parse(json);
        if (root.Value<string>("Format") != "AirTablet.AutoGreet.Venue" || root.Value<int?>("Version") != 1 || export.Venue is null)
            throw new InvalidDataException("Unsupported AutoGreet venue profile format or version.");
        var venue = export.Venue;
        venue.GreetingProfiles ??= [];
        venue.VipTiers ??= [];
        venue.ActiveVipMacroIdsByTier ??= [];
        venue.CustomRegionMacroRoutes ??= [];
        export.Regions ??= [];
        if (venue.GreetingProfiles.Count == 0 || venue.GreetingProfiles.Any(profile => profile is null || profile.Macros is null || profile.Macros.Any(macro => macro is null)) ||
            venue.VipTiers.Any(tier => tier is null) || venue.CustomRegionMacroRoutes.Any(route => route is null) || export.Regions.Any(region => region is null))
            throw new InvalidDataException("The venue contains missing or invalid profile entries.");

        var ids = new Dictionary<Guid, Guid>();
        Guid Remap(Guid id) => id == Guid.Empty ? Guid.Empty : ids.TryGetValue(id, out var replacement)
            ? replacement : ids[id] = Guid.NewGuid();
        // Validate stable identities before adding anything to live storage.
        var ownedIds = venue.GreetingProfiles.Select(profile => profile.Id)
            .Concat(venue.GreetingProfiles.SelectMany(profile => profile.Macros).Select(macro => macro.Id))
            .Concat(venue.VipTiers.Select(tier => tier.Id)).Concat(export.Regions.Select(region => region.Id))
            .Concat(venue.CustomRegionMacroRoutes.Select(route => route.Id)).ToList();
        if (ownedIds.Any(id => id == Guid.Empty) || ownedIds.Distinct().Count() != ownedIds.Count)
            throw new InvalidDataException("The venue contains duplicate or missing entry identifiers.");

        venue.Id = Guid.NewGuid();
        var requested = string.IsNullOrWhiteSpace(venue.Name) ? "Imported Venue" : venue.Name.Trim();
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        venue.Name = requested;
        for (var suffix = 2; names.Contains(venue.Name); suffix++) venue.Name = $"{requested} {suffix}";
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in venue.GreetingProfiles)
        {
            var profileName = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
            profile.Name = profileName;
            for (var suffix = 2; !profileNames.Add(profile.Name); suffix++) profile.Name = $"{profileName} {suffix}";
            profile.Id = Remap(profile.Id);
            foreach (var macro in profile.Macros) macro.Id = Remap(macro.Id);
        }
        venue.ActiveGreetingProfileId = Remap(venue.ActiveGreetingProfileId);
        venue.ActiveFirstTimeMacroId = Remap(venue.ActiveFirstTimeMacroId);
        venue.ActiveReturningMacroId = Remap(venue.ActiveReturningMacroId);
        venue.ActiveVipMacroId = Remap(venue.ActiveVipMacroId);
        venue.ActiveBlacklistedMacroId = Guid.Empty;
        venue.DefaultVipTierId = Remap(venue.DefaultVipTierId);
        foreach (var tier in venue.VipTiers) tier.Id = Remap(tier.Id);
        venue.ActiveVipMacroIdsByTier = venue.ActiveVipMacroIdsByTier.ToDictionary(pair => Remap(pair.Key), pair => Remap(pair.Value));
        foreach (var region in export.Regions)
        {
            region.Id = Remap(region.Id);
            // Regions are global in AutoGreet. Keep new regions disabled until
            // reviewed so import cannot expand the currently active venue's area.
            region.Enabled = false;
        }
        venue.DoorbellRegionId = Remap(venue.DoorbellRegionId);
        venue.VisitorListRegionId = Remap(venue.VisitorListRegionId);
        venue.FirstTimeGreetingRegionId = Remap(venue.FirstTimeGreetingRegionId);
        venue.ReturningGreetingRegionId = Remap(venue.ReturningGreetingRegionId);
        venue.VipGreetingRegionId = Remap(venue.VipGreetingRegionId);
        foreach (var route in venue.CustomRegionMacroRoutes)
        {
            route.Id = Remap(route.Id);
            route.RegionId = Remap(route.RegionId);
            route.MacroId = Remap(route.MacroId);
        }
        venue.PlotLock ??= new();
        venue.PlotLock.LocationKind ??= string.Empty;
        ClearRuntimeData(venue);
        return export;
    }

    private static IEnumerable<Guid> RegionIds(VenueProfile venue)
        => new[] { venue.DoorbellRegionId, venue.VisitorListRegionId, venue.FirstTimeGreetingRegionId, venue.ReturningGreetingRegionId, venue.VipGreetingRegionId }
            .Concat(venue.CustomRegionMacroRoutes.Select(route => route.RegionId));

    private static T Clone<T>(T source) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(source, Settings), Settings)!;

    private static void ClearRuntimeData(VenueProfile venue)
    {
        venue.LifetimeVisitors = new(StringComparer.OrdinalIgnoreCase);
        venue.Session = new();
        venue.Queue = [];
        venue.Blacklist = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StoredPropertiesResolver : DefaultContractResolver
    {
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization serialization)
            => base.CreateProperties(type, serialization).Where(property => property.Writable).ToList();
    }
}
