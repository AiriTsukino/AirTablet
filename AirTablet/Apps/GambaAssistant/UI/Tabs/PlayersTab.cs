using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class PlayersTab
{
    private readonly BlackjackSession session;
    private readonly PlayerSessionService players;
    private readonly DealerLedgerService ledger;
    private readonly Dictionary<string, long> manualDeposits = new();

    public PlayersTab(BlackjackSession session, PlayerSessionService players, DealerLedgerService ledger)
    {
        this.session = session;
        this.players = players;
        this.ledger = ledger;
    }

    public void Draw()
    {
        UiHelpers.InfoBox("Session-Scoped Banks", "This tab only shows player banks. Dealer starting/ending gil and house ledger values are managed on the Dealer Ledger tab.");

        var bankPlayers = session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer).OrderBy(p => p.PartySlot).ToList();
        if (bankPlayers.Count == 0)
        {
            UiHelpers.Card("No Players", () => ImGui.TextDisabled("No non-dealer party players are currently tracked."));
            return;
        }

        foreach (var player in bankPlayers)
        {
            ImGui.PushID(player.DisplayName);
            UiHelpers.Card($"{player.PartySlot}. {player.DisplayName}", () =>
            {
                ImGui.Text($"Status: {player.Status}");
                ImGui.Text($"Bank: {player.Bank.Available:N0} available + {player.Bank.ActiveBet:N0} active bet = {player.Bank.TotalTracked:N0} total");
                ImGui.TextDisabled($"Last trade: {player.Bank.LastTradeAmount:N0} gil | Last bet: {player.Bank.LastBet:N0} gil");

                var key = player.Identity.ToString();
                manualDeposits.TryGetValue(key, out var deposit);
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(140));
                if (UiHelpers.InputGil("Manual deposit", ref deposit))
                    manualDeposits[key] = deposit;

                ImGui.SameLine();
                if (ImGui.Button("Add to Bank"))
                {
                    players.AddBankDeposit(player, deposit);
                    var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
                    if (dealer is not null && deposit > 0)
                    {
                        ledger.RecordTrade(new TradeEntry
                        {
                            From = player.Identity,
                            To = dealer.Identity,
                            Amount = deposit,
                            Classification = TradeClassification.BuyInBankDeposit,
                            Phase = session.Round.Phase.ToString(),
                            Note = "Players tab manual deposit",
                            Manual = true
                        });
                    }
                }
            });
            ImGui.PopID();
        }
    }
}
