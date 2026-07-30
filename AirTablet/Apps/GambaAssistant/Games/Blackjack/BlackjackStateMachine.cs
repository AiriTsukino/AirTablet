using GambaAssistant.Models.Games;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Games.Blackjack;

public sealed class BlackjackStateMachine : IGameStateMachine
{
    private readonly BlackjackSession session;
    public BlackjackStateMachine(BlackjackSession session) => this.session = session;

    public bool CanExecute(IGameAction action, out string reason)
    {
        reason = string.Empty;
        if (action is not BlackjackAction blackjackAction) { reason = "Unknown action."; return false; }
        var phase = session.Round.Phase;
        var hand = session.ActiveHand;
        switch (blackjackAction.Kind)
        {
            case BlackjackActionKind.OpenBetting: return Require(phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands, "Betting can only open between hands.", out reason);
            case BlackjackActionKind.Deal: return Require(phase == BlackjackPhase.BettingOpen && session.Round.Players.Any(p => p.BetConfirmed), "At least one player needs a confirmed valid bet.", out reason);
            case BlackjackActionKind.Hit: return Require(phase == BlackjackPhase.PlayerTurns && hand is { IsComplete: false }, "Hit is only available for the active unfinished hand.", out reason);
            case BlackjackActionKind.Stand: return Require(phase == BlackjackPhase.PlayerTurns && hand is { IsComplete: false }, "Stand is only available for the active unfinished hand.", out reason);
            case BlackjackActionKind.DoubleDown: return CanDouble(hand, out reason);
            case BlackjackActionKind.Split: return CanSplit(hand, out reason);
            case BlackjackActionKind.SettleRound: return Require(phase == BlackjackPhase.Settlement, "Round can only settle after dealer turn finishes.", out reason);
            case BlackjackActionKind.ResetNight: return true;
            default: reason = "Action is not valid in the current phase."; return false;
        }
    }

    public void Execute(IGameAction action)
    {
        if (!CanExecute(action, out var reason)) throw new InvalidOperationException(reason);
        if (action is not BlackjackAction blackjackAction) return;
        switch (blackjackAction.Kind)
        {
            case BlackjackActionKind.OpenBetting: session.Round.Phase = BlackjackPhase.BettingOpen; break;
            case BlackjackActionKind.Stand: if (session.ActiveHand is { } h) { h.IsComplete = true; h.Actions.Add("Stand"); } AdvanceToNextHand(); break;
            case BlackjackActionKind.ResetNight: session.ResetNight(); break;
        }
    }

    public bool CanDouble(BlackjackHand? hand, out string reason)
    {
        reason = string.Empty;
        if (session.Round.Phase != BlackjackPhase.PlayerTurns || hand == null || hand.IsComplete) { reason = "Double Down is only available for the active unfinished hand."; return false; }
        if (hand.Cards.Count != 2) { reason = "Double Down is only available on the first two cards by default."; return false; }
        if (hand.IsSplitHand && !session.Rules.DoubleAfterSplit) { reason = "Rules disable Double Down on split hands."; return false; }
        if (session.Rules.DoubleOnlyOnNineTenEleven && hand.BestTotal is not (9 or 10 or 11)) { reason = "Rules restrict Double Down to totals of 9, 10, or 11."; return false; }
        var player = session.ActivePlayer;
        if (player == null || player.Bank.Available < hand.Bet) { reason = "Player needs enough available bank to reserve the extra matching bet."; return false; }
        return true;
    }

    public bool CanSplit(BlackjackHand? hand, out string reason)
    {
        if (!CanSplitStructurally(hand, out reason))
            return false;

        var player = session.ActivePlayer;
        if (player == null)
        {
            reason = "No active player is available for this split.";
            return false;
        }

        if (player.Bank.Available < hand!.Bet)
        {
            reason = "Player needs enough available bank for the extra split bet.";
            return false;
        }

        return true;
    }

    public bool CanSplitStructurally(BlackjackHand? hand, out string reason)
    {
        reason = string.Empty;
        if (!session.Rules.SplittingEnabled) { reason = "Splitting is disabled by this profile."; return false; }
        if (session.Round.Phase != BlackjackPhase.PlayerTurns || hand == null || hand.IsComplete) { reason = "Split is only available for the active unfinished hand."; return false; }
        if (hand.Cards.Count != 2) { reason = "Split requires exactly two cards."; return false; }
        if (hand.IsSplitHand && !session.Rules.ResplitPairs) { reason = "Rules allow a hand to be split only once."; return false; }

        // Every exact pair is always splittable: A+A, 1+1, 5+5, K+K, etc.
        // The existing value-pair rule can additionally allow different ten-value
        // ranks, such as K+Q, when that profile option is enabled.
        var cardsMatch = hand.CanSplitByExactRank
                         || (session.Rules.SplitByValue && hand.CanSplitByValue);
        if (!cardsMatch) { reason = "Cards must be a matching pair."; return false; }

        var player = session.ActivePlayer;
        if (player == null) { reason = "No active player is available for this split."; return false; }
        if (player.Hands.Count(h => !h.IsVoided && h.Cards.Count > 0) >= session.Rules.MaxSplitHands) { reason = "Maximum split hands reached."; return false; }
        return true;
    }

    public void AdvanceToNextHand()
    {
        var players = session.Round.Players;
        for (var pi = session.Round.ActivePlayerIndex; pi < players.Count; pi++)
        {
            var startHand = pi == session.Round.ActivePlayerIndex ? session.Round.ActiveHandIndex + 1 : 0;
            for (var hi = startHand; hi < players[pi].Hands.Count; hi++)
            {
                if (!players[pi].Hands[hi].IsComplete)
                {
                    session.Round.ActivePlayerIndex = pi;
                    session.Round.ActiveHandIndex = hi;
                    return;
                }
            }
        }
        session.Round.Phase = BlackjackPhase.DealerTurn;
    }

    private static bool Require(bool ok, string message, out string reason)
    {
        reason = ok ? string.Empty : message;
        return ok;
    }
}
