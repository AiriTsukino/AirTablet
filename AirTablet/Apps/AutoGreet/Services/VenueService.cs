using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class VenueService
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;

    public VenueService(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
        _ = ActiveVenue;
        RepairAllVenueData(save: true);
    }

    public VenueProfile ActiveVenue
    {
        get
        {
            if (persistence.Venues.Count == 0)
            {
                var venue = VenueProfile.CreateDefault("Default Venue");
                persistence.Venues.Add(venue);
                config.ActiveVenueId = venue.Id;
            }

            var active = persistence.Venues.FirstOrDefault(v => v.Id == config.ActiveVenueId);
            if (active is not null) return active;

            config.ActiveVenueId = persistence.Venues[0].Id;
            return persistence.Venues[0];
        }
    }

    public bool IsVenueActive => !config.ActiveVenueDisabled && persistence.Venues.Any(v => v.Id == config.ActiveVenueId);

    public VenueProfile? ActiveVenueOrNull => IsVenueActive
        ? persistence.Venues.FirstOrDefault(v => v.Id == config.ActiveVenueId)
        : null;

    public IReadOnlyList<VenueProfile> Venues => persistence.Venues;

    public IEnumerable<(VenueProfile Venue, GreetingProfile Profile)> AllGreetingProfiles =>
        persistence.Venues.SelectMany(v => v.GreetingProfiles.Select(p => (v, p)));

    public GreetingProfile GetGreetingProfileForVenue(VenueProfile venue)
    {
        var active = AllGreetingProfiles.FirstOrDefault(x => x.Profile.Id == venue.ActiveGreetingProfileId).Profile;
        if (active is not null) return active;

        if (venue.GreetingProfiles.Count == 0)
        {
            var profile = GreetingProfile.CreateDefault();
            venue.GreetingProfiles.Add(profile);
            venue.ActiveGreetingProfileId = profile.Id;
            return profile;
        }

        venue.ActiveGreetingProfileId = venue.GreetingProfiles[0].Id;
        return venue.GreetingProfiles[0];
    }

    public GreetingMacro? GetActiveMacro(VenueProfile venue, GreetingCategory category)
    {
        var profile = GetGreetingProfileForVenue(venue);
        var activeId = venue.GetActiveMacroId(category);
        return activeId == Guid.Empty
            ? null
            : profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == category && m.Id == activeId);
    }

    public VenueProfile CreateVenue(string name)
    {
        var venue = VenueProfile.CreateDefault(string.IsNullOrWhiteSpace(name) ? "New Venue" : name.Trim());
        persistence.Venues.Add(venue);
        config.ActiveVenueId = venue.Id;
        config.ActiveVenueDisabled = false;
        RepairVenueData(venue);
        persistence.SaveNow();
        return venue;
    }

    public void SwitchVenue(Guid id)
    {
        if (id == Guid.Empty)
        {
            config.ActiveVenueDisabled = true;
            persistence.SaveNow();
            return;
        }

        if (persistence.Venues.Any(v => v.Id == id))
        {
            config.ActiveVenueId = id;
            config.ActiveVenueDisabled = false;
            persistence.SaveNow();
        }
    }

    public void RenameVenue(Guid id, string newName)
    {
        var venue = persistence.Venues.FirstOrDefault(v => v.Id == id);
        if (venue is null || string.IsNullOrWhiteSpace(newName)) return;
        venue.Name = newName.Trim();
        persistence.SaveNow();
    }

    public void DeleteVenue(Guid id)
    {
        if (persistence.Venues.Count <= 1) return;
        var venue = persistence.Venues.FirstOrDefault(v => v.Id == id);
        if (venue is null) return;
        persistence.Venues.Remove(venue);
        if (config.ActiveVenueId == id)
        {
            config.ActiveVenueId = persistence.Venues[0].Id;
            config.ActiveVenueDisabled = false;
        }
        persistence.SaveNow();
    }

    public void RepairAllVenueData(bool save = true)
    {
        if (persistence.Venues.Count == 0)
        {
            var venue = VenueProfile.CreateDefault("Default Venue");
            persistence.Venues.Add(venue);
            config.ActiveVenueId = venue.Id;
        }

        foreach (var venue in persistence.Venues)
            RepairVenueData(venue);

        if (persistence.Venues.All(v => v.Id != config.ActiveVenueId))
            config.ActiveVenueId = persistence.Venues[0].Id;

        if (save) persistence.SaveNow();
    }

    public void RepairActiveVenueData(bool save = true)
    {
        RepairVenueData(ActiveVenue);
        if (save) persistence.SaveNow();
    }

    public void RepairVenueData(VenueProfile venue)
    {
        if (venue.GreetingProfiles.Count == 0)
        {
            var profile = GreetingProfile.CreateDefault();
            venue.GreetingProfiles.Add(profile);
            venue.ActiveGreetingProfileId = profile.Id;
        }

        // Collapse accidental duplicate greeting profiles created by earlier builds/reloads.
        // Earlier builds could create new Guid values for the same user profile on every reload.
        // Exact fingerprint matching was not enough once the user edited only one copy, so this
        // intentionally treats profile names as unique within a venue and merges macros into the
        // profile that should survive.
        venue.GreetingProfiles = MergeDuplicateGreetingProfiles(venue);

        foreach (var profile in venue.GreetingProfiles)
        {
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
            profile.Macros = profile.Macros
                .Where(m => m is not null && m.Category != GreetingCategory.Blacklisted)
                .GroupBy(MacroFingerprint, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (profile.Macros.Count == 0)
                profile.Macros.AddRange(GreetingProfile.CreateDefault().Macros);
        }

        if (venue.GreetingProfiles.All(p => p.Id != venue.ActiveGreetingProfileId) && !AllGreetingProfiles.Any(x => x.Profile.Id == venue.ActiveGreetingProfileId))
            venue.ActiveGreetingProfileId = venue.GreetingProfiles[0].Id;

        EnsureActiveMacroSelection(venue, GreetingCategory.FirstTime);
        EnsureActiveMacroSelection(venue, GreetingCategory.Returning);
        RepairVipTierData(venue);
        venue.ActiveBlacklistedMacroId = Guid.Empty;
        if (venue.VisitorListRegionId == Guid.Empty && venue.DoorbellRegionId != Guid.Empty)
            venue.VisitorListRegionId = venue.DoorbellRegionId;

        venue.PlotLock ??= new VenuePlotLock();
        RepairPlotLock(venue.PlotLock);
        RepairCustomRegionMacroRoutes(venue);

        // Collapse duplicate session rows/lists by Name@World, case-insensitively.
        venue.Session.NightlyVisitors = venue.Session.NightlyVisitors
            .Where(v => !string.IsNullOrWhiteSpace(v.Key.Name) && !string.IsNullOrWhiteSpace(v.Key.World))
            .GroupBy(v => v.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(v => v.LastSeenUtc).First())
            .ToList();

        venue.Session.Greeted = DistinctKeys(venue.Session.Greeted);
        venue.Session.Skipped = DistinctKeys(venue.Session.Skipped);
        venue.Session.Ungreeted = DistinctKeys(venue.Session.Ungreeted)
            .Where(k => !ContainsKey(venue.Session.Greeted, k) && !ContainsKey(venue.Session.Skipped, k))
            .ToList();

        // The queue is an active work queue, not history. On plugin reload, remove stale completed/failed/cancelled rows.
        venue.Queue = venue.Queue
            .Where(q => q.Status is QueueEntryStatus.Waiting or QueueEntryStatus.Running)
            .GroupBy(q => $"{q.Visitor}::{q.CustomRegionRouteId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(q => q.EnqueuedUtc).First())
            .ToList();

        foreach (var q in venue.Queue.Where(q => q.Status == QueueEntryStatus.Running))
        {
            q.Status = QueueEntryStatus.Waiting;
            q.StatusText = "Restored after reload";
        }
    }


    private static void RepairPlotLock(VenuePlotLock plotLock)
    {
        if (string.IsNullOrWhiteSpace(plotLock.HousingDistrict))
        {
            var territory = plotLock.OriginalHouseTerritoryType != 0 ? plotLock.OriginalHouseTerritoryType : plotLock.TerritoryType;
            var district = HousingLocationFormatter.GetKnownHousingDistrictFromTerritory(territory);
            if (!string.IsNullOrWhiteSpace(district))
                plotLock.HousingDistrict = district;
        }

        if (string.IsNullOrWhiteSpace(plotLock.LocationKind))
        {
            if (plotLock.Room >= 0)
                plotLock.LocationKind = VenuePlotLock.LocationKindApartment;
            else if (plotLock.Plot >= 0)
                plotLock.LocationKind = VenuePlotLock.LocationKindPlot;
        }
    }

    private void RepairCustomRegionMacroRoutes(VenueProfile venue)
    {
        venue.PlotLock ??= new VenuePlotLock();
        venue.CustomRegionMacroRoutes ??= [];
        var profile = GetGreetingProfileForVenue(venue);
        var enabledMacroIds = profile.Macros
            .Where(m => m.Enabled && m.Category != GreetingCategory.Blacklisted)
            .Select(m => m.Id)
            .ToHashSet();
        var regionIds = new HashSet<Guid>(persistence.CustomRegions.Select(r => r.Id));

        venue.CustomRegionMacroRoutes = venue.CustomRegionMacroRoutes
            .Where(r => r is not null && r.Id != Guid.Empty)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();

        foreach (var route in venue.CustomRegionMacroRoutes)
        {
            route.Name = string.IsNullOrWhiteSpace(route.Name) ? "Region macro" : route.Name.Trim();
            if (route.RegionId != Guid.Empty && !regionIds.Contains(route.RegionId))
                route.Enabled = false;
            if (route.MacroId != Guid.Empty && !enabledMacroIds.Contains(route.MacroId))
                route.MacroId = Guid.Empty;
        }
    }

    private void RepairVipTierData(VenueProfile venue)
    {
        venue.VipTiers ??= [];
        venue.ActiveVipMacroIdsByTier ??= [];
        venue.VipTiers = venue.VipTiers
            .Where(t => t is not null && t.Id != Guid.Empty)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        if (venue.VipTiers.Count == 0)
            venue.VipTiers.Add(new VipTierDefinition());

        if (venue.VipTiers.All(t => t.Id != venue.DefaultVipTierId))
            venue.DefaultVipTierId = venue.VipTiers[0].Id;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in venue.VipTiers)
        {
            var baseName = string.IsNullOrWhiteSpace(tier.Name) ? "VIP" : tier.Name.Trim();
            var name = baseName;
            var suffix = 2;
            while (!usedNames.Add(name))
                name = $"{baseName} ({suffix++})";
            tier.Name = name;
        }

        var defaultTierId = venue.DefaultVipTierId;
        var validTierIds = venue.VipTiers.Select(t => t.Id).ToHashSet();
        foreach (var visitor in venue.LifetimeVisitors.Values)
        {
            if (visitor.Vip || visitor.VipTierId != Guid.Empty)
            {
                visitor.Vip = true;
                if (!validTierIds.Contains(visitor.VipTierId))
                    visitor.VipTierId = defaultTierId;
            }
            else
            {
                visitor.VipTierId = Guid.Empty;
            }
        }

        var profile = GetGreetingProfileForVenue(venue);
        var enabledVipMacros = profile.Macros
            .Where(m => m.Enabled && m.Category == GreetingCategory.Vip)
            .ToList();
        var validVipMacroIds = enabledVipMacros.Select(m => m.Id).ToHashSet();

        foreach (var tierId in venue.ActiveVipMacroIdsByTier.Keys.ToArray())
        {
            if (!validTierIds.Contains(tierId) || !validVipMacroIds.Contains(venue.ActiveVipMacroIdsByTier[tierId]))
                venue.ActiveVipMacroIdsByTier.Remove(tierId);
        }

        if (!venue.ActiveVipMacroIdsByTier.ContainsKey(defaultTierId)
            && venue.ActiveVipMacroId != Guid.Empty
            && validVipMacroIds.Contains(venue.ActiveVipMacroId))
        {
            venue.ActiveVipMacroIdsByTier[defaultTierId] = venue.ActiveVipMacroId;
        }

        if (!venue.ActiveVipMacroIdsByTier.ContainsKey(defaultTierId))
        {
            var fallback = enabledVipMacros.FirstOrDefault()?.Id ?? Guid.Empty;
            if (fallback != Guid.Empty)
                venue.ActiveVipMacroIdsByTier[defaultTierId] = fallback;
        }

        venue.ActiveVipMacroId = venue.GetActiveVipMacroId(defaultTierId);
    }

    private static List<GreetingProfile> MergeDuplicateGreetingProfiles(VenueProfile venue)
    {
        var merged = new List<GreetingProfile>();
        foreach (var group in venue.GreetingProfiles
                     .Where(p => p is not null)
                     .GroupBy(p => NormalizeProfileName(p.Name), StringComparer.OrdinalIgnoreCase))
        {
            // Prefer the currently active copy when possible; otherwise keep the copy with
            // the most macros, since that is usually the user-edited profile.
            var keeper = group.FirstOrDefault(p => p.Id == venue.ActiveGreetingProfileId)
                         ?? group.OrderByDescending(p => p.Macros.Count).First();

            keeper.Name = string.IsNullOrWhiteSpace(keeper.Name) ? "Profile" : keeper.Name.Trim();

            foreach (var duplicate in group.Where(p => p.Id != keeper.Id))
            {
                foreach (var macro in duplicate.Macros.Where(m => m is not null))
                {
                    if (keeper.Macros.Any(existing => string.Equals(MacroFingerprint(existing), MacroFingerprint(macro), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // If a macro with the same ID somehow already exists, give the imported
                    // macro a new ID before merging it.
                    if (keeper.Macros.Any(existing => existing.Id == macro.Id))
                        macro.Id = Guid.NewGuid();

                    keeper.Macros.Add(macro);
                }
            }

            merged.Add(keeper);
        }

        return merged;
    }

    private static string NormalizeProfileName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Profile" : name.Trim();

    private void EnsureActiveMacroSelection(VenueProfile venue, GreetingCategory category)
    {
        var profile = GetGreetingProfileForVenue(venue);
        var enabledMacros = profile.Macros
            .Where(m => m.Enabled && m.Category == category)
            .ToList();
        var activeId = venue.GetActiveMacroId(category);
        if (activeId != Guid.Empty && enabledMacros.Any(m => m.Id == activeId))
            return;

        venue.SetActiveMacroId(category, enabledMacros.FirstOrDefault()?.Id ?? Guid.Empty);
    }

    private static List<VisitorKey> DistinctKeys(IEnumerable<VisitorKey> keys)
    {
        return keys
            .Where(k => !string.IsNullOrWhiteSpace(k.Name) && !string.IsNullOrWhiteSpace(k.World))
            .GroupBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public static bool ContainsKey(IEnumerable<VisitorKey> keys, VisitorKey key) =>
        keys.Any(k => string.Equals(k.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));

    public static void RemoveKey(List<VisitorKey> keys, VisitorKey key) =>
        keys.RemoveAll(k => string.Equals(k.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));

    private static string ProfileFingerprint(GreetingProfile profile)
    {
        var macroText = string.Join("|", profile.Macros.Select(MacroFingerprint).Order(StringComparer.OrdinalIgnoreCase));
        return $"{profile.Name.Trim()}::{macroText}";
    }

    private static string MacroFingerprint(GreetingMacro macro) =>
        $"{macro.Category}::{macro.Name.Trim()}::{macro.Script.Replace("\r", string.Empty).Trim()}";
}
