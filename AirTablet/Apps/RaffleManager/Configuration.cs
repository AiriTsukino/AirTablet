using Dalamud.Configuration;
using RaffleManager.Models;

namespace RaffleManager;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;
    public bool WindowVisible { get; set; } = true;
    public bool SettingsWindowVisible { get; set; }
    public string DataDirectory { get; set; } = string.Empty;
    public string ActiveVenueProfile { get; set; } = "Default";
    public float LeftPanelWidth { get; set; } = 440f;

    // Backwards-compatible fields from early builds. Values are migrated into the Default profile on first load.
    public string VenueName { get; set; } = "Default";
    public string CustomLogoPath { get; set; } = string.Empty;
    public int TicketPrice { get; set; } = 100_000;
    public int BaseJackpot { get; set; } = 5_000_000;
    public int WinnerSplitPercent { get; set; } = 50;
    public bool BogoBonusTicketsCountTowardJackpot { get; set; }
    public bool AnnounceWinner { get; set; } = true;
    public WinnerChatChannel AnnouncementChannel { get; set; } = WinnerChatChannel.Yell;
    public string WinnerMessageTemplate { get; set; } = "Congratulations {winner}! You won {payout} from the {jackpot} raffle jackpot!";
    public bool TickSoundEnabled { get; set; } = true;
    public float TickVolume { get; set; } = 0.30f;

    internal Dictionary<string, VenueProfile> VenueProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal VenueProfile Profile => GetOrCreateProfile(ActiveVenueProfile);

    internal VenueProfile GetOrCreateProfile(string name)
    {
        name = SanitizeProfileName(name);
        if (!VenueProfiles.TryGetValue(name, out var profile))
        {
            profile = new VenueProfile { Name = name, VenueName = name };
            VenueProfiles[name] = profile;
        }

        profile.Name = name;
        EnsureProfileDefaults(profile);
        return profile;
    }

    internal void EnsureDefaults()
    {
        ActiveVenueProfile = SanitizeProfileName(ActiveVenueProfile);
        if (VenueProfiles.Count == 0)
        {
            VenueProfiles["Default"] = new VenueProfile
            {
                Name = "Default",
                VenueName = string.IsNullOrWhiteSpace(VenueName) ? "Default" : VenueName,
                CustomLogoPath = CustomLogoPath ?? string.Empty,
                TicketPrice = Math.Max(0, TicketPrice),
                BaseJackpot = Math.Max(0, BaseJackpot),
                WinnerSplitPercent = Math.Clamp(WinnerSplitPercent, 1, 100),
                BogoBonusTicketsCountTowardJackpot = BogoBonusTicketsCountTowardJackpot,
                AnnounceWinner = AnnounceWinner,
                AnnouncementChannel = AnnouncementChannel,
                WinnerMessageTemplate = string.IsNullOrWhiteSpace(WinnerMessageTemplate)
                    ? "Congratulations {winner}! You won {payout} from the {jackpot} raffle jackpot!"
                    : WinnerMessageTemplate,
                TickSoundEnabled = TickSoundEnabled,
                TickVolume = Math.Clamp(TickVolume, 0f, 1f),
            };
        }

        if (!VenueProfiles.ContainsKey(ActiveVenueProfile))
            ActiveVenueProfile = "Default";

        foreach (var pair in VenueProfiles.ToArray())
        {
            var cleanName = SanitizeProfileName(pair.Key);
            pair.Value.Name = cleanName;
            EnsureProfileDefaults(pair.Value);
            if (!cleanName.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                VenueProfiles.Remove(pair.Key);
                VenueProfiles[cleanName] = pair.Value;
            }
        }

        LeftPanelWidth = Math.Clamp(LeftPanelWidth <= 0 ? 440f : LeftPanelWidth, 360f, 900f);
    }

    internal static string SanitizeProfileName(string? name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return "Default";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }

    internal static void EnsureProfileDefaults(VenueProfile profile)
    {
        profile.Name = SanitizeProfileName(profile.Name);
        profile.VenueName = string.IsNullOrWhiteSpace(profile.VenueName) ? profile.Name : profile.VenueName.Trim();
        profile.CustomLogoPath ??= string.Empty;
        profile.TicketPrice = Math.Max(0, profile.TicketPrice);
        profile.BaseJackpot = Math.Max(0, profile.BaseJackpot);
        profile.WinnerSplitPercent = Math.Clamp(profile.WinnerSplitPercent, 1, 100);
        profile.TickVolume = Math.Clamp(profile.TickVolume, 0f, 1f);
        profile.MainWindowLeftPanelRatio = profile.MainWindowLeftPanelRatio <= 0f
            ? 0.33f
            : Math.Clamp(profile.MainWindowLeftPanelRatio, 0.20f, 0.80f);
        profile.WinnerMessageTemplate = string.IsNullOrWhiteSpace(profile.WinnerMessageTemplate)
            ? "Congratulations {winner}! You won {payout} from the {jackpot} raffle jackpot!"
            : profile.WinnerMessageTemplate;
        profile.Data ??= new RaffleState();
        profile.Data.Entries ??= new List<RaffleEntry>();
        profile.Data.History ??= new List<RaffleEntry[]>();
        profile.Data.WinnerHistory ??= new List<WinnerRecord>();
    }
}
