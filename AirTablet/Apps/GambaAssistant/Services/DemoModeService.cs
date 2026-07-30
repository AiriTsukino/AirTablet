using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class DemoModeService
{
    private readonly BlackjackSession session;
    private readonly ChatQueueService chat;
    private readonly LogService log;
    public DemoModeService(BlackjackSession session, ChatQueueService chat, LogService log) { this.session = session; this.chat = chat; this.log = log; }

    public void StartDemo()
    {
        chat.DemoMode = true;
        session.ResetNight();
        session.SessionPlayers.Add(new PlayerSessionState { Identity = new PlayerIdentity("Dealer", "Demo"), PartySlot = 1, Status = PlayerStatus.Dealer });
        session.SessionPlayers.Add(new PlayerSessionState { Identity = new PlayerIdentity("Alice", "Demo"), PartySlot = 2, Status = PlayerStatus.SittingOut, Bank = { Available = 100_000 } });
        session.SessionPlayers.Add(new PlayerSessionState { Identity = new PlayerIdentity("Bob", "Demo"), PartySlot = 3, Status = PlayerStatus.SittingOut, Bank = { Available = 100_000 } });
        session.Round.Phase = BlackjackPhase.CashOutBetweenHands;
        log.Add(LogCategory.Demo, "Started isolated demo mode. No party chat, live banks, trades, or exports are affected.");
    }

    public void StopDemo()
    {
        chat.DemoMode = false;
        session.ResetNight();
        log.Add(LogCategory.Demo, "Stopped demo mode and cleared demo session state.");
    }
}
