using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class DealerLedgerService
{
    private readonly BlackjackSession session;
    private readonly LogService log;
    public DealerLedgerService(BlackjackSession session, LogService log) { this.session = session; this.log = log; }
    public DealerLedger Ledger => session.DealerLedger;

    public long OutstandingPlayerBanks => session.SessionPlayers
        .Where(p => p.Status != PlayerStatus.Dealer)
        .GroupBy(p => NormalizeName(p.Identity.Name))
        .Sum(g => g.Max(p => p.Bank.TotalTracked));

    public long LiveGameProfitLoss => Ledger.TotalBuyInsDeposits - Ledger.TotalCashOuts - OutstandingPlayerBanks;
    public long BankTrackingDifference => LiveGameProfitLoss - Ledger.GameProfitLoss;

    public long ExpectedDealerGil => Ledger.ExpectedDealerGil;

    public long? Difference => Ledger.ActualEndingGil.HasValue
        ? Ledger.ActualEndingGil.Value - ExpectedDealerGil
        : null;

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var characterName = value.Trim();
        var atIndex = characterName.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
            characterName = characterName[..atIndex];

        Span<char> buffer = stackalloc char[Math.Min(characterName.Length, 64)];
        var count = 0;
        foreach (var ch in characterName)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;
            if (count >= buffer.Length)
                break;
            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
    }

    public void RecordTrade(TradeEntry entry)
    {
        Ledger.Trades.Add(entry);
        switch (entry.Classification)
        {
            case TradeClassification.BuyInBankDeposit: Ledger.TotalBuyInsDeposits += entry.Amount; break;
            case TradeClassification.CashOut: Ledger.TotalCashOuts += entry.Amount; break;
            case TradeClassification.DealerTip: Ledger.DealerTips += entry.Amount; break;
            case TradeClassification.VenueTip: Ledger.VenueTips += entry.Amount; break;
        }
        log.Add(LogCategory.Trades, $"{entry.From.Display} → {entry.To.Display}: {entry.Amount:N0} gil | {entry.Phase} | {entry.Classification}");
    }

    public void ApplySettlement(RoundHistoryEntry entry)
    {
        // PlayerDelta is the net movement in the player bank for this individual
        // hand after the reserved wager is resolved. Apply every hand separately so
        // split hands, double-downs, pushes, and naturals reconcile correctly.
        Ledger.GameProfitLoss -= entry.PlayerDelta;

        if (entry.PlayerDelta > 0)
            Ledger.TotalPayouts += entry.TotalReturn;
        else if (entry.PlayerDelta < 0)
            Ledger.TotalBets += entry.Bet;
    }

    public void ApplySettlementDelta(long playerDelta)
    {
        Ledger.GameProfitLoss -= playerDelta;
        if (playerDelta > 0) Ledger.TotalPayouts += playerDelta;
        else if (playerDelta < 0) Ledger.TotalBets += -playerDelta;
    }

    public void ManualAdjustment(PlayerIdentity player, long amount, AdjustmentType type, string reason)
    {
        Ledger.Adjustments.Add(new AdjustmentEntry { Player = player, Amount = amount, Type = type, Reason = reason });
        Ledger.MiscAdjustments += amount;
        log.Add(LogCategory.Info, $"Manual adjustment {amount:N0} gil for {player.Display}: {type} — {reason}");
    }

    public void RecordTip(long amount, TradeClassification tipType, string note)
    {
        if (amount <= 0)
        {
            log.Add(LogCategory.Warnings, "Tip amount must be greater than 0 gil.");
            return;
        }

        if (tipType is not TradeClassification.DealerTip and not TradeClassification.VenueTip)
            tipType = TradeClassification.DealerTip;

        Ledger.Tips.Add(new TipEntry
        {
            Player = PlayerIdentity.UnknownDealer(),
            Amount = amount,
            TipType = tipType,
        });

        if (tipType == TradeClassification.DealerTip)
            Ledger.DealerTips += amount;
        else
            Ledger.VenueTips += amount;

        var label = tipType == TradeClassification.DealerTip ? "dealer" : "venue";
        log.Add(LogCategory.Info, $"Recorded {label} tip {amount:N0} gil{(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}");
    }
}
