namespace AutoGreet.Models;

[Serializable]
public sealed class VenueProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Venue";
    public Dictionary<string, Visitor> LifetimeVisitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SessionData Session { get; set; } = new();
    public List<GreetingProfile> GreetingProfiles { get; set; } = [];
    public Guid ActiveGreetingProfileId { get; set; }
    public Guid ActiveFirstTimeMacroId { get; set; }
    public Guid ActiveReturningMacroId { get; set; }
    public Guid ActiveVipMacroId { get; set; }
    public Guid ActiveBlacklistedMacroId { get; set; }
    public Guid DefaultVipTierId { get; set; }
    public List<VipTierDefinition> VipTiers { get; set; } = [];
    public Dictionary<Guid, Guid> ActiveVipMacroIdsByTier { get; set; } = [];
    public HashSet<string> Blacklist { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<QueueEntry> Queue { get; set; } = [];
    public VenuePlotLock PlotLock { get; set; } = new();
    public List<CustomRegionMacroRoute> CustomRegionMacroRoutes { get; set; } = [];

    // Optional region routing. Guid.Empty means AutoGreet's default area:
    // whole housing interior when inside housing, or any enabled custom region in non-housing zones.
    public Guid DoorbellRegionId { get; set; }
    public Guid VisitorListRegionId { get; set; }
    public Guid FirstTimeGreetingRegionId { get; set; }
    public Guid ReturningGreetingRegionId { get; set; }
    public Guid VipGreetingRegionId { get; set; }

    public GreetingProfile ActiveGreetingProfile
    {
        get
        {
            if (GreetingProfiles.Count == 0)
            {
                var profile = GreetingProfile.CreateDefault();
                GreetingProfiles.Add(profile);
                ActiveGreetingProfileId = profile.Id;
            }

            var active = GreetingProfiles.FirstOrDefault(x => x.Id == ActiveGreetingProfileId);
            if (active is not null) return active;
            ActiveGreetingProfileId = GreetingProfiles[0].Id;
            return GreetingProfiles[0];
        }
    }

    public Guid GetActiveMacroId(GreetingCategory category) => category switch
    {
        GreetingCategory.FirstTime => ActiveFirstTimeMacroId,
        GreetingCategory.Returning => ActiveReturningMacroId,
        GreetingCategory.Vip => ActiveVipMacroId,
        GreetingCategory.Blacklisted => Guid.Empty,
        _ => Guid.Empty,
    };

    public void SetActiveMacroId(GreetingCategory category, Guid id)
    {
        switch (category)
        {
            case GreetingCategory.FirstTime:
                ActiveFirstTimeMacroId = id;
                break;
            case GreetingCategory.Returning:
                ActiveReturningMacroId = id;
                break;
            case GreetingCategory.Vip:
                ActiveVipMacroId = id;
                break;
            case GreetingCategory.Blacklisted:
                // Blacklisted greeting macros are intentionally unsupported.
                ActiveBlacklistedMacroId = Guid.Empty;
                break;
        }
    }

    public VipTierDefinition GetDefaultVipTier()
    {
        var tier = VipTiers.FirstOrDefault(x => x.Id == DefaultVipTierId)
                   ?? VipTiers.FirstOrDefault();
        if (tier is not null)
        {
            DefaultVipTierId = tier.Id;
            return tier;
        }

        tier = new VipTierDefinition();
        VipTiers.Add(tier);
        DefaultVipTierId = tier.Id;
        return tier;
    }

    public VipTierDefinition? GetVipTier(Guid tierId) =>
        VipTiers.FirstOrDefault(x => x.Id == tierId);

    public Guid GetActiveVipMacroId(Guid tierId) =>
        ActiveVipMacroIdsByTier.TryGetValue(tierId, out var macroId)
            ? macroId
            : Guid.Empty;

    public void SetActiveVipMacroId(Guid tierId, Guid macroId)
    {
        if (tierId == Guid.Empty)
            return;

        if (macroId == Guid.Empty)
            ActiveVipMacroIdsByTier.Remove(tierId);
        else
            ActiveVipMacroIdsByTier[tierId] = macroId;

        if (tierId == DefaultVipTierId)
            ActiveVipMacroId = macroId;
    }

    public static VenueProfile CreateDefault(string name)
    {
        var profile = GreetingProfile.CreateDefault();
        var defaultVipTier = new VipTierDefinition();
        var venue = new VenueProfile
        {
            Name = name,
            GreetingProfiles = [profile],
            ActiveGreetingProfileId = profile.Id,
            DefaultVipTierId = defaultVipTier.Id,
            VipTiers = [defaultVipTier],
        };
        venue.ActiveFirstTimeMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.FirstTime)?.Id ?? Guid.Empty;
        venue.ActiveReturningMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.Returning)?.Id ?? Guid.Empty;
        venue.ActiveVipMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.Vip)?.Id ?? Guid.Empty;
        if (venue.ActiveVipMacroId != Guid.Empty)
            venue.ActiveVipMacroIdsByTier[defaultVipTier.Id] = venue.ActiveVipMacroId;
        venue.ActiveBlacklistedMacroId = Guid.Empty;
        return venue;
    }
}
