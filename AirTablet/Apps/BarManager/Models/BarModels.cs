namespace BarManager.Models;

public enum GambaRunDirection
{
    CountUp,
    CountDown,
}

[Serializable]
public sealed class DrinkDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Drink";
    public int Price { get; set; } = 0;
    public bool IsGambaDrink { get; set; }
    public bool Enabled { get; set; } = true;
}

[Serializable]
public sealed class DrinkSale
{
    public Guid DrinkId { get; set; }
    public int Count { get; set; }
    public int CountBeforeBuyout { get; set; }
    public int CountCoveredByBuyout { get; set; }
}

[Serializable]
public sealed class GambaRule
{
    public string Name { get; set; } = "New Rule";
    public string Tier { get; set; } = "CUSTOM";
    public bool Enabled { get; set; } = true;
    public int Payout { get; set; }
    public bool PaysJackpot { get; set; }
    public bool GrantsFreeRoll { get; set; }
    public int? EqualTo { get; set; }
    public List<int> InValues { get; set; } = new();
    public List<string> ContainsTokens { get; set; } = new();
    public List<int> ContainsAnyDigits { get; set; } = new();
    public bool Triples { get; set; }
    public bool AdjacentDoubles { get; set; }
    public bool Runs { get; set; }
    public bool? RunsCountUp { get; set; }
    public bool? RunsCountDown { get; set; }
    // Retained so rules saved before independent direction toggles preserve their selected direction.
    public GambaRunDirection RunDirection { get; set; } = GambaRunDirection.CountUp;
    public bool ExactOnly { get; set; } = true;
    public string WinningRollExpression { get; set; } = string.Empty;
    public string LastTooltipRollExpression { get; set; } = string.Empty;
    public string WinningRollsTooltip { get; set; } = string.Empty;
    public string ExactOnlyTooltip { get; set; } = string.Empty;
}

[Serializable]
public sealed class GambaRollRangeMultiplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public int MinimumRoll { get; set; } = 1;
    public int MaximumRoll { get; set; } = 499;
    public float Multiplier { get; set; } = 2f;
    public bool AppliesToJackpot { get; set; }
}

[Serializable]
public sealed class GambaSettings
{
    public string DrinkName { get; set; } = "Gamba Drink";
    public int RollsPerDrink { get; set; } = 1;
    public int MinRoll { get; set; } = 1;
    public int MaxRoll { get; set; } = 999;
    public bool AutoEndWhenRollsUsed { get; set; } = false;
    public bool AllowPasteImport { get; set; } = true;
    public bool AnnounceRollsLeft { get; set; } = true;
    public int AnnounceEveryRolls { get; set; } = 5;
    public bool AddRollPricePercentToJackpot { get; set; }
    public float JackpotContributionPercent { get; set; }
    public bool AutoEndOnJackpotWin { get; set; } = false;
    public bool ShowRollPurchaseGuidanceAfterJackpotWin { get; set; } = false;
    public bool JackpotShoutoutEnabled { get; set; } = true;
    public string JackpotShoutoutChannel { get; set; } = "yell";
    public string JackpotShoutoutMessage { get; set; } = "Congratulations {player}! They just won the jackpot for {payout} gil!";
    public bool LossStreakBonusEnabled { get; set; }
    public int LossStreakThreshold { get; set; } = 5;
    public float LossStreakBonusMultiplier { get; set; } = 2f;
    public string LossStreakBonusName { get; set; } = "Loss Streak Bonus";
    public string LossStreakBonusAnnouncement { get; set; } = "{player}, {bonus} is active! Your next win is multiplied by x{multiplier} {duration}.";
    public bool LossStreakBonusAppliesToJackpot { get; set; } = false;
    public int? LossStreakBonusDurationTurns { get; set; } = null;
    public bool BartenderRollBonusEnabled { get; set; }
    public int BartenderRollMax { get; set; } = 999;
    public float BartenderRollBonusMultiplier { get; set; } = 2f;
    public string BartenderRollBonusName { get; set; } = "Bartender Bonus";
    public string BartenderRollBonusAnnouncement { get; set; } = "{player}, {bonus} is active! Your next win is multiplied by x{multiplier} {duration}.";
    public bool BartenderRollBonusAppliesToJackpot { get; set; } = false;
    public int? BartenderRollBonusDurationTurns { get; set; } = 3;
    public List<GambaRollRangeMultiplier> RollRangeMultipliers { get; set; } = new();
    public List<GambaRule> Rules { get; set; } = Defaults();

    public static List<GambaRule> Defaults() => new();
}

[Serializable]
public sealed class VenueProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default Venue";
    public List<DrinkDefinition> Drinks { get; set; } = new();
    public int JackpotBase { get; set; } = 1_000_000;
    public int FullBuyoutPrice { get; set; } = 4_000_000;
    public int HourlyBuyoutPrice { get; set; } = 1_000_000;
    public GambaSettings Gamba { get; set; } = new();

    public static List<DrinkDefinition> DefaultDrinks() => new();
}

[Serializable]
public sealed class BarManagerData
{
    public List<VenueProfile> Venues { get; set; } = new() { new VenueProfile() };
    public BarAuditState CurrentAudit { get; set; } = new();
}


[Serializable]
public sealed class VenueProfileExport
{
    public string ExportedBy { get; set; } = "BarManager";
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public VenueProfile Venue { get; set; } = new();
}

[Serializable]
public sealed class GambaSettingsExport
{
    public string ExportedBy { get; set; } = "BarManager";
    public int FormatVersion { get; set; } = 3;
    public string VenueName { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public GambaSettings Gamba { get; set; } = new();
}

[Serializable]
public sealed class GambaRollRecord
{
    public int Roll { get; set; }
    public string Tier { get; set; } = "NONE";
    public int Payout { get; set; }
    public bool JackpotWin { get; set; }
    public bool FreeRoll { get; set; }
    public int JackpotContribution { get; set; }
    public string BonusName { get; set; } = string.Empty;
    public float BonusMultiplier { get; set; } = 1f;
    public int BasePayout { get; set; }
}

[Serializable]
public sealed class GambaSessionRecord
{
    public string CustomerName { get; set; } = "Unknown";
    public string CustomerWorld { get; set; } = string.Empty;
    public int DrinksPurchased { get; set; }
    public int RollsAllowed { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public List<GambaRollRecord> Rolls { get; set; } = new();
    public int ConsecutiveLosses { get; set; }
    public bool LossStreakBonusActive { get; set; }
    public int LossStreakBonusTurnsRemaining { get; set; }
    public bool BartenderRollBonusActive { get; set; }
    public int BartenderRollBonusTurnsRemaining { get; set; }
    public int TotalPayout => Rolls.Sum(r => r.Payout);
    public int TotalJackpotContributions => Rolls.Sum(r => r.JackpotContribution);
    public bool JackpotWon => Rolls.Any(r => r.JackpotWin);
    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerWorld) ? CustomerName : $"{CustomerName}@{CustomerWorld}";
}

[Serializable]
public sealed class BarBuyoutRecord
{
    public DateTime SubmittedAt { get; set; } = DateTime.Now;
    public string Buyer { get; set; } = string.Empty;
    public string Type { get; set; } = "full";
    public float Hours { get; set; }
    public int CustomPrice { get; set; }
    public int Total { get; set; }
    public string DisplayType => Type switch
    {
        "hourly" => $"Hourly ({Hours:0.##}h)",
        "custom" => "Custom",
        _ => "Full night",
    };
}

[Serializable]
public sealed class BarAuditState
{
    public string BartenderName { get; set; } = string.Empty;
    public DateTime BusinessDate { get; set; } = DateTime.Today;
    public int MyStartingGil { get; set; }
    public int VenuePrizeGil { get; set; }
    public int Tips { get; set; }
    public int PrizesPaidOut { get; set; }
    public int EndingGilEntered { get; set; }
    public int JackpotCurrent { get; set; } = 1_000_000;
    public bool BarBuyoutActive { get; set; }
    public string BarBuyoutType { get; set; } = "full";
    public float BarBuyoutHours { get; set; }
    public string BarBuyoutBuyer { get; set; } = string.Empty;
    public int BarBuyoutCustomPrice { get; set; }
    public List<BarBuyoutRecord> SubmittedBuyouts { get; set; } = new();
    public List<DrinkSale> DrinkSales { get; set; } = new();
    public List<GambaSessionRecord> GambaSessions { get; set; } = new();
}

public sealed record RollResolution(string Tier, int Payout, bool JackpotWin, bool FreeRoll);
