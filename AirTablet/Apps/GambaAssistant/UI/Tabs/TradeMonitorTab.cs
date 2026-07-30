using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class TradeMonitorTab
{
    private readonly BlackjackSession session;
    private readonly TradeMonitorService trades;
    private readonly Dictionary<string, long> manualAmounts = new();
    private int playerIndex;

    public TradeMonitorTab(BlackjackSession session, TradeMonitorService trades)
    {
        this.session = session;
        this.trades = trades;
    }

    public void Draw()
    {
        UiHelpers.InfoBox("Detection Status", trades.DetectionStatus);

        var activePlayers = session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer).ToList();
        UiHelpers.Card("Manual Trade Entry", () =>
        {
            if (activePlayers.Count == 0)
            {
                ImGui.TextDisabled("No non-dealer players are currently tracked.");
                return;
            }

            var names = activePlayers.Select(p => p.DisplayName).ToArray();
            ImGui.Combo("Player", ref playerIndex, names, names.Length);
            playerIndex = Math.Clamp(playerIndex, 0, activePlayers.Count - 1);

            var player = activePlayers[playerIndex];
            var key = player.Identity.ToString();
            manualAmounts.TryGetValue(key, out var amount);
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(140));
            if (UiHelpers.InputGil("Amount", ref amount))
                manualAmounts[key] = amount;

            if (ImGui.Button("Manual Buy-in / Bank Deposit"))
                trades.AddManualTrade(player.Identity, session.SessionPlayers.First().Identity, amount, TradeClassification.BuyInBankDeposit);

            ImGui.SameLine();
            if (ImGui.Button("Manual Cash-Out"))
                trades.AddManualTrade(session.SessionPlayers.First().Identity, player.Identity, amount, TradeClassification.CashOut);
        });

        UiHelpers.Card("Recent Trades", () =>
        {
            var count = 0;
            foreach (var trade in trades.Trades.OrderByDescending(t => t.Timestamp).Take(50))
            {
                count++;
                ImGui.TextWrapped($"{trade.Timestamp:t} | {trade.From.Display} → {trade.To.Display}: {trade.Amount:N0} gil | {trade.Phase} | {trade.Classification} | {trade.Note}");
            }

            if (count == 0)
                ImGui.TextDisabled("No detected or manual trades yet.");
        });
    }
}
