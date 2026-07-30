using GambaAssistant.Models.Players;

namespace GambaAssistant.Models.Ledger;

public enum TradeClassification { Bet, BuyInBankDeposit, CashOut, DealerTip, VenueTip, MiscCorrection, IgnoreDuplicateInvalid, NeedsReview }
public enum AdjustmentType { TradeCorrection, PayoutCorrection, VenueComp, VoidRefund, Other }

[Serializable]
public sealed class TradeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public PlayerIdentity From { get; set; }
    public PlayerIdentity To { get; set; }
    public long Amount { get; set; }
    public string Phase { get; set; } = string.Empty;
    public TradeClassification Classification { get; set; } = TradeClassification.NeedsReview;
    public string Note { get; set; } = string.Empty;
    public bool Manual { get; set; }
}

[Serializable]
public sealed class AdjustmentEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public PlayerIdentity Player { get; set; }
    public long Amount { get; set; }
    public AdjustmentType Type { get; set; }
    public string Reason { get; set; } = string.Empty;
}

[Serializable]
public sealed class TipEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public PlayerIdentity Player { get; set; }
    public long Amount { get; set; }
    public TradeClassification TipType { get; set; } = TradeClassification.DealerTip;
}

[Serializable]
public sealed class RoundHistoryEntry
{
    public int RoundNumber { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Player { get; set; } = string.Empty;
    public string DealerCards { get; set; } = string.Empty;
    public string PlayerCards { get; set; } = string.Empty;
    public long Bet { get; set; }
    public long TotalReturn { get; set; }
    public long PlayerDelta { get; set; }
    public string Actions { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public long BankAfter { get; set; }
}

[Serializable]
public sealed class DealerLedger
{
    public long StartingGil { get; set; }
    public long GameProfitLoss { get; set; }
    public long DealerTips { get; set; }
    public long VenueTips { get; set; }
    public long MiscAdjustments { get; set; }
    public long TotalBuyInsDeposits { get; set; }
    public long TotalBets { get; set; }
    public long TotalPayouts { get; set; }
    public long TotalCashOuts { get; set; }
    public long ExpectedDealerGil => StartingGil + GameProfitLoss + DealerTips + VenueTips + MiscAdjustments;
    public long? ActualEndingGil { get; set; }
    public long? Difference => ActualEndingGil.HasValue ? ActualEndingGil.Value - ExpectedDealerGil : null;
    public List<TradeEntry> Trades { get; set; } = [];
    public List<AdjustmentEntry> Adjustments { get; set; } = [];
    public List<TipEntry> Tips { get; set; } = [];
}

[Serializable]
public sealed class PlayerLedger
{
    public PlayerIdentity Player { get; set; }
    public List<RoundHistoryEntry> Rounds { get; set; } = [];
    public List<TradeEntry> Trades { get; set; } = [];
    public List<AdjustmentEntry> Adjustments { get; set; } = [];
}
