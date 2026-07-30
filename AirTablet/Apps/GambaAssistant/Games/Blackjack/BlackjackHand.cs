namespace GambaAssistant.Games.Blackjack;

[Serializable]
public sealed class BlackjackHand
{
    public int HandNumber { get; set; } = 1;
    public List<BlackjackCard> Cards { get; set; } = [];
    public long Bet { get; set; }
    public long OriginalBet { get; set; }
    public bool IsComplete { get; set; }
    public bool IsBusted { get; set; }
    public bool IsDoubled { get; set; }
    public bool IsSplitHand { get; set; }
    public bool IsVoided { get; set; }
    public List<string> Actions { get; set; } = [];
    public int BestTotal
    {
        get
        {
            var total = Cards.Sum(c => c.HardValue);
            var aces = Cards.Count(c => c.IsAce);
            while (aces-- > 0 && total + 10 <= 21) total += 10;
            return total;
        }
    }
    public bool IsSoft => Cards.Any(c => c.IsAce) && Cards.Sum(c => c.HardValue) + 10 <= 21;
    public bool IsTwoCardTwentyOne => Cards.Count == 2 && Cards.Any(c => c.IsAce) && Cards.Any(c => c.IsTenValue);
    public bool IsNaturalBlackjack => !IsSplitHand && IsTwoCardTwentyOne;
    public bool CanSplitByExactRank => Cards.Count == 2 && Cards[0].Rank == Cards[1].Rank;
    public bool CanSplitByValue => Cards.Count == 2 && Cards[0].HardValue == Cards[1].HardValue;
    public string CardText => Cards.Count == 0 ? "—" : string.Join(" + ", Cards.Select(c => c.ToString()));
    public string TotalText => Cards.Count == 0 ? "—" : IsNaturalBlackjack ? "Natural Blackjack" : $"{(IsSoft ? "Soft " : string.Empty)}{BestTotal}";
    public void AddCard(BlackjackCard card)
    {
        Cards.Add(card);
        if (BestTotal > 21) { IsBusted = true; IsComplete = true; }
        else if (BestTotal == 21) IsComplete = true;
        if (IsNaturalBlackjack) IsComplete = true;
    }
}
