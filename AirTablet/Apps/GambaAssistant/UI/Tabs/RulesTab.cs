using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class RulesTab
{
    private readonly BlackjackSession session;
    private readonly ProfileService profiles;

    public RulesTab(BlackjackSession session, ProfileService profiles)
    {
        this.session = session;
        this.profiles = profiles;
    }

    public void Draw()
    {
        var rules = session.Rules;

        UiHelpers.Card("Active Profile", () =>
        {
            ImGui.Text($"Profile: {profiles.ActiveProfile.Name}");
            if (session.IsActive)
                ImGui.TextDisabled("Critical rules and profile settings are locked while a night/session is active.");
        });

        UiHelpers.Card("Table Limits", () =>
        {
            ImGui.Text($"Minimum bet: {rules.MinimumBet:N0} gil");
            ImGui.Text($"Maximum bet: {rules.MaximumBet:N0} gil");
            ImGui.Text($"Standard win return: {rules.StandardWinTotalMultiplier:0.00}x original bet");
            ImGui.Text($"Double Down win return: {rules.DoubleDownWinTotalMultiplier:0.00}x original bet");
            ImGui.Text($"Natural Blackjack return: {rules.NaturalBlackjackTotalMultiplier:0.00}x original bet");
        });

        UiHelpers.Card("Blackjack Rules", () =>
        {
            ImGui.TextWrapped($"Cards are generated from visible party-scoped {rules.DiceCommand} rolls. Mapping: 1=A, 11=J, 12=Q, 13=K. No suits. Infinite/deckless model; duplicates are allowed.");
            ImGui.Text($"Initial deal mode: {InitialDealModeLabel(rules.InitialDealMode)}");
            ImGui.Text($"Tie result: {(rules.PushOnTie ? "Push" : "Dealer wins")}");
            ImGui.Text($"Split enabled: {rules.SplittingEnabled}");
            ImGui.Text($"Split hands can be split again: {rules.ResplitPairs}");
            ImGui.Text($"Maximum hands after splits: {rules.MaxSplitHands}");
            ImGui.Text($"Double Down on split hands: {rules.DoubleAfterSplit}");
            ImGui.Text($"21 on split hand counts as Natural Blackjack: {rules.SplitTwentyOneCountsAsNatural}");
            ImGui.Text($"Dealer stands on all 17s including soft 17: {rules.DealerStandsOnSoft17}");
        });
    }

    private static string InitialDealModeLabel(BlackjackInitialDealMode mode) => mode switch
    {
        BlackjackInitialDealMode.PlayerFullHandsThenDealer => "Full player hands first, dealer visible card last",
        _ => "Round-robin"
    };
}
