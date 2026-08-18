using BarManager.Models;
using Dalamud.Configuration;

namespace BarManager;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool WindowVisible { get; set; }
    public bool SettingsWindowVisible { get; set; }
    public Guid ActiveVenueId { get; set; }
    public string DataDirectory { get; set; } = string.Empty;
    public string AuditReportDirectory { get; set; } = string.Empty;
    public string GambaSettingsDirectory { get; set; } = string.Empty;

    internal BarManagerData Data { get; set; } = new();
    internal List<VenueProfile> Venues => Data.Venues;

    internal BarAuditState CurrentAudit
    {
        get => Data.CurrentAudit;
        set => Data.CurrentAudit = value;
    }

    internal VenueProfile ActiveVenue
    {
        get
        {
            if (Venues.Count == 0)
                Venues.Add(new VenueProfile());
            var venue = Venues.FirstOrDefault(v => v.Id == ActiveVenueId) ?? Venues[0];
            ActiveVenueId = venue.Id;
            return venue;
        }
    }

    internal void EnsureDefaults()
    {
        if (Venues.Count == 0)
            Venues.Add(new VenueProfile());
        if (ActiveVenueId == Guid.Empty || Venues.All(v => v.Id != ActiveVenueId))
            ActiveVenueId = Venues[0].Id;
        var venue = ActiveVenue;
        if (CurrentAudit.JackpotCurrent <= 0 || CurrentAudit.JackpotCurrent == 20_000_000)
            CurrentAudit.JackpotCurrent = venue.JackpotBase;
        foreach (var drink in venue.Drinks)
        {
            if (CurrentAudit.DrinkSales.All(s => s.DrinkId != drink.Id))
                CurrentAudit.DrinkSales.Add(new DrinkSale { DrinkId = drink.Id });
        }
        foreach (var profile in Venues)
        {
            profile.Gamba ??= new GambaSettings();
            profile.Gamba.Rules ??= new List<GambaRule>();
            profile.Gamba.RollRangeMultipliers ??= new List<GambaRollRangeMultiplier>();
            var gameMinimum = Math.Max(0, profile.Gamba.MinRoll);
            var gameMaximum = Math.Max(gameMinimum, profile.Gamba.MaxRoll);
            foreach (var rule in profile.Gamba.Rules)
            {
                rule.MinimumWinningRoll = Math.Clamp(rule.MinimumWinningRoll, gameMinimum, gameMaximum);
                rule.MaximumWinningRoll = Math.Clamp(rule.MaximumWinningRoll, rule.MinimumWinningRoll, gameMaximum);
            }
            foreach (var range in profile.Gamba.RollRangeMultipliers)
            {
                if (range.Id == Guid.Empty) range.Id = Guid.NewGuid();
                range.MinimumRoll = Math.Clamp(range.MinimumRoll, gameMinimum, gameMaximum);
                range.MaximumRoll = Math.Clamp(range.MaximumRoll, range.MinimumRoll, gameMaximum);
                range.Multiplier = Math.Clamp(range.Multiplier, 1f, 100f);
            }
        }
    }
}
