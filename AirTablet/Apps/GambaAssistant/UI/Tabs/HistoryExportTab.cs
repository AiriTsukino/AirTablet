using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class HistoryExportTab
{
    private readonly BlackjackSession session;
    private readonly ExportService exports;
    private string last = string.Empty;

    public HistoryExportTab(BlackjackSession session, ExportService exports)
    {
        this.session = session;
        this.exports = exports;
    }

    public void Draw()
    {
        UiHelpers.Card("Manual Export", () =>
        {
            if (ImGui.Button("Export JSON"))
                last = exports.ExportJson();

            ImGui.SameLine();
            if (ImGui.Button("Export CSV"))
                last = exports.ExportCsv();

            if (!string.IsNullOrEmpty(last))
                ImGui.TextWrapped($"Last export: {last}");
        });

        UiHelpers.Card("Current-Night Round History", () =>
        {
            var count = 0;
            foreach (var player in session.SessionPlayers)
            {
                foreach (var round in player.RoundHistory.TakeLast(100))
                {
                    count++;
                    ImGui.TextWrapped($"Round {round.RoundNumber} | {round.Player} | {round.PlayerCards} vs {round.DealerCards} | Bet {round.Bet:N0} | {round.Outcome} | Bank {round.BankAfter:N0}");
                }
            }

            if (count == 0)
                ImGui.TextDisabled("No settled hands recorded yet.");
        });
    }
}
