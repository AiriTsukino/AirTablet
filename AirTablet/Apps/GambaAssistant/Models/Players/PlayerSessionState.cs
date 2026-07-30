using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;

namespace GambaAssistant.Models.Players;

[Serializable]
public sealed class PlayerSessionState
{
    public PlayerIdentity Identity { get; set; }
    public int PartySlot { get; set; }
    public PlayerStatus Status { get; set; } = PlayerStatus.SittingOut;
    public PlayerBank Bank { get; set; } = new();
    public List<BlackjackHand> Hands { get; set; } = [];
    public List<RoundHistoryEntry> RoundHistory { get; set; } = [];
    public bool BetConfirmed { get; set; }
    public string DisplayName => Identity.Display;
    public bool IsActiveForHand => Status == PlayerStatus.Playing && BetConfirmed && Hands.Count > 0;
}
