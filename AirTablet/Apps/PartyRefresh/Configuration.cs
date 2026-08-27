using Dalamud.Configuration;

namespace PartyRefresh;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string ActiveVenueProfileId { get; set; } = string.Empty;
    public bool SettingsVisible { get; set; }
    public bool AutoRefreshEnabled { get; set; }
    public int RefreshIntervalMinutes { get; set; } = 50;

    public void Normalize()
    {
        ActiveVenueProfileId ??= string.Empty;
        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes, 1, 55);
    }
}

internal enum PartyRefreshRole
{
    Free,
    Tank,
    Healer,
    MeleeDps,
    PhysicalRangedDps,
    MagicalRangedDps,
    Omit,
}

internal sealed class PartyFinderPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Venue Party Finder";
    public int RecruitmentType { get; set; }
    public int DutyCategoryId { get; set; }
    public uint DutyRowId { get; set; }
    public string DutyName { get; set; } = "None";
    public int ObjectiveId { get; set; }
    public string Comment { get; set; } = string.Empty;
    public List<PartyRefreshRole> Slots { get; set; } =
    [
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
        PartyRefreshRole.Free,
    ];
    public bool RemoveRoleRestrictions { get; set; }
    public bool UnselectClasses { get; set; }
    public bool OnePlayerPerJob { get; set; }
    public bool LimitRecruitingToWorld { get; set; }
    public bool FormPrivateParty { get; set; }
    public int PrivatePartyPassword { get; set; }
    public bool CompletionStatusEnabled { get; set; }
    public int CompletionStatusType { get; set; }
    public bool AvgItemLevelEnabled { get; set; } = true;
    public int AvgItemLevel { get; set; } = 999;
    public bool UnrestrictedParty { get; set; }
    public bool MinimumItemLevel { get; set; }
    public bool SilenceEcho { get; set; }
    public int LootRules { get; set; }
    public bool Japanese { get; set; } = true;
    public bool English { get; set; } = true;
    public bool German { get; set; } = true;
    public bool French { get; set; } = true;

    public void Normalize()
    {
        Id = Id == Guid.Empty ? Guid.NewGuid() : Id;
        Name = CleanName(Name, "Venue Party Finder");
        RecruitmentType = Math.Clamp(RecruitmentType, 0, 2);
        DutyCategoryId = Math.Clamp(DutyCategoryId, 0, 15);
        DutyName = string.IsNullOrWhiteSpace(DutyName) ? "None" : DutyName.Trim();
        ObjectiveId = Math.Clamp(ObjectiveId, 0, 3);
        Comment ??= string.Empty;
        Comment = TruncateUtf8(Comment.Replace('\r', ' ').Replace('\n', ' '), 191);
        Slots ??= [];
        while (Slots.Count < 8)
            Slots.Add(PartyRefreshRole.Free);
        if (Slots.Count > 8)
            Slots = Slots.Take(8).ToList();
        PrivatePartyPassword = Math.Clamp(PrivatePartyPassword, 0, 9999);
        CompletionStatusType = Math.Clamp(CompletionStatusType, 0, 2);
        AvgItemLevel = Math.Clamp(AvgItemLevel, 1, 999);
        LootRules = Math.Clamp(LootRules, 0, 2);
    }

    public PartyFinderPreset Clone(string name)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<PartyFinderPreset>(
            System.Text.Json.JsonSerializer.Serialize(this)) ?? new PartyFinderPreset();
        copy.Id = Guid.NewGuid();
        copy.Name = CleanName(name, $"{Name} Copy");
        copy.Normalize();
        return copy;
    }

    internal static string CleanName(string? value, string fallback)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return result.Length > 64 ? result[..64] : result;
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var candidate = builder.ToString() + rune;
            if (System.Text.Encoding.UTF8.GetByteCount(candidate) > maximumBytes)
                break;
            builder.Append(rune);
        }
        return builder.ToString();
    }
}

internal sealed class VenueProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default Venue";
    public Guid ActivePresetId { get; set; }
    public List<PartyFinderPreset> Presets { get; set; } = [new PartyFinderPreset()];

    public PartyFinderPreset ActivePreset =>
        Presets.FirstOrDefault(candidate => candidate.Id == ActivePresetId) ?? Presets[0];

    public void Normalize()
    {
        Id = Id == Guid.Empty ? Guid.NewGuid() : Id;
        Name = PartyFinderPreset.CleanName(Name, "Default Venue");
        Presets ??= [];
        foreach (var preset in Presets)
            preset.Normalize();
        if (Presets.Count == 0)
            Presets.Add(new PartyFinderPreset());
        if (Presets.All(candidate => candidate.Id != ActivePresetId))
            ActivePresetId = Presets[0].Id;
    }
}
