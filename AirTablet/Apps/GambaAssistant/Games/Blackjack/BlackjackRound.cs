using GambaAssistant.Models.Players;

namespace GambaAssistant.Games.Blackjack;

public enum BlackjackPhase { Idle, BettingOpen, Dealing, PlayerTurns, DealerTurn, Settlement, CashOutBetweenHands }

[Serializable]
public sealed class BlackjackRound
{
    public int RoundNumber { get; set; } = 1;
    public BlackjackPhase Phase { get; set; } = BlackjackPhase.Idle;
    public BlackjackHand DealerHand { get; set; } = new() { HandNumber = 0 };
    public int ActivePlayerIndex { get; set; }
    public int ActiveHandIndex { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public List<PlayerSessionState> Players { get; set; } = [];
}
