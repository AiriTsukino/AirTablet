using GambaAssistant.Models.Games;

namespace GambaAssistant.Games.Blackjack;

[Serializable]
public sealed class BlackjackRules : IGameRuleset
{
    public string DisplayName { get; set; } = "Default venue Blackjack";
    public long MinimumBet { get; set; } = 10_000;
    public long MaximumBet { get; set; } = 500_000;
    public string DiceCommand { get; set; } = "/dice party 13";
    public int DiceSides { get; set; } = 13;
    public BlackjackInitialDealMode InitialDealMode { get; set; } = BlackjackInitialDealMode.PlayerFullHandsThenDealer;
    public BlackjackPayout NaturalBlackjackPayout { get; set; } = BlackjackPayout.Custom;
    public bool DealerStandsOnSoft17 { get; set; } = true;
    public bool PushOnTie { get; set; } = true;
    public bool SplittingEnabled { get; set; } = true;
    public bool SplitByExactRank { get; set; } = true;
    public bool SplitByValue { get; set; }
    public int MaxSplitHands { get; set; } = 2;
    public bool DoubleAfterSplit { get; set; } = false;
    public bool ResplitPairs { get; set; } = false;
    public bool ResplitAces { get; set; }
    public bool SplitAcesOneCardOnly { get; set; } = true;
    public bool SplitTwentyOneCountsAsNatural { get; set; } = false;
    public bool DoubleOnAnyTwoCards { get; set; } = true;
    public bool DoubleOnlyOnNineTenEleven { get; set; }
    public decimal CustomBlackjackMultiplier { get; set; } = 2.5m;

    // These are total return multipliers against the original hand bet, not just
    // profit. Example defaults: 10k standard win returns 20k; double-down win
    // returns 30k; natural Blackjack returns 35k.
    public decimal StandardWinTotalMultiplier { get; set; } = 2.0m;
    public decimal DoubleDownWinTotalMultiplier { get; set; } = 3.0m;
    public decimal NaturalBlackjackTotalMultiplier { get; set; } = 3.5m;

    public long StandardWinReturn(long originalBet) => Multiply(originalBet, StandardWinTotalMultiplier);
    public long DoubleDownWinReturn(long originalBet) => Multiply(originalBet, DoubleDownWinTotalMultiplier);
    public long NaturalBlackjackReturn(long originalBet) => Multiply(originalBet, NaturalBlackjackTotalMultiplier);

    public long NaturalWinnings(long bet) => NaturalBlackjackReturn(bet) - bet;

    private static long Multiply(long bet, decimal multiplier)
        => Math.Max(0, (long)Math.Round(bet * multiplier));
}

public enum BlackjackPayout { ThreeToTwo, SixToFive, TwoToOne, Custom }

public enum BlackjackInitialDealMode
{
    RoundRobin,
    PlayerFullHandsThenDealer
}
