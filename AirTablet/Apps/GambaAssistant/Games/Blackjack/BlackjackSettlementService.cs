using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Games.Blackjack;

public sealed class BlackjackSettlementService
{
    private readonly BlackjackRules rules;
    public BlackjackSettlementService(BlackjackRules rules) => this.rules = rules;

    public RoundHistoryEntry Settle(PlayerSessionState player, BlackjackHand hand, BlackjackHand dealer, int roundNumber)
    {
        var outcome = Resolve(hand, dealer);
        var wager = hand.Bet;
        var originalBet = GetOriginalBet(hand);

        var totalReturn = outcome switch
        {
            BlackjackOutcome.Win => hand.IsDoubled
                ? rules.DoubleDownWinReturn(originalBet)
                : rules.StandardWinReturn(originalBet),
            BlackjackOutcome.NaturalBlackjack => rules.NaturalBlackjackReturn(originalBet),
            BlackjackOutcome.Push => wager,
            BlackjackOutcome.Loss => 0,
            BlackjackOutcome.Void => wager,
            _ => 0
        };

        // Active bet was already reserved from available bank. Return exactly the
        // configured total payout. Double-down pushes therefore return both the
        // initial and DD stakes, while losses keep the full wager removed.
        var playerDelta = totalReturn - wager;
        player.Bank.ActiveBet = Math.Max(0, player.Bank.ActiveBet - wager);
        if (totalReturn > 0)
            player.Bank.Available += totalReturn;

        return new RoundHistoryEntry
        {
            RoundNumber = roundNumber,
            Player = player.DisplayName,
            DealerCards = dealer.CardText,
            PlayerCards = hand.CardText,
            Bet = hand.Bet,
            TotalReturn = totalReturn,
            PlayerDelta = playerDelta,
            Actions = string.Join(", ", hand.Actions),
            Outcome = outcome.ToString(),
            BankAfter = player.Bank.Available
        };
    }

    public BlackjackOutcome Resolve(BlackjackHand player, BlackjackHand dealer)
    {
        if (player.IsVoided) return BlackjackOutcome.Void;
        if (player.IsBusted) return BlackjackOutcome.Loss;

        var playerNatural = IsNatural(player);
        var dealerNatural = dealer.IsNaturalBlackjack;
        if (playerNatural && dealerNatural) return BlackjackOutcome.Push;
        if (playerNatural) return BlackjackOutcome.NaturalBlackjack;
        if (dealerNatural) return BlackjackOutcome.Loss;

        if (dealer.IsBusted) return BlackjackOutcome.Win;
        if (player.BestTotal > dealer.BestTotal) return BlackjackOutcome.Win;
        if (player.BestTotal == dealer.BestTotal) return rules.PushOnTie ? BlackjackOutcome.Push : BlackjackOutcome.Loss;
        return BlackjackOutcome.Loss;
    }

    private bool IsNatural(BlackjackHand hand)
        => hand.IsNaturalBlackjack || (rules.SplitTwentyOneCountsAsNatural && hand.IsSplitHand && hand.IsTwoCardTwentyOne);

    private static long GetOriginalBet(BlackjackHand hand)
    {
        if (hand.OriginalBet > 0)
            return hand.OriginalBet;

        return hand.IsDoubled && hand.Bet > 1 ? hand.Bet / 2 : hand.Bet;
    }
}

public enum BlackjackOutcome { Win, Loss, Push, NaturalBlackjack, Void }
