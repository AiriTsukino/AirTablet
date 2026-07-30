using GambaAssistant.Models.Games;

namespace GambaAssistant.Games.Blackjack;

public enum BlackjackActionKind { StartNight, OpenBetting, ConfirmBet, Deal, Hit, Stand, DoubleDown, Split, DealerDraw, SettleRound, CashOut, VoidHand, ResetNight }
public sealed record BlackjackAction(BlackjackActionKind Kind, string Name) : IGameAction;
