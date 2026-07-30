using Dalamud.Bindings.ImGui;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class DealerLedgerTab
{
    private readonly DealerLedgerService service;
    private long tipAmountToAdd;
    private int selectedTipType;
    private string tipNote = string.Empty;

    public DealerLedgerTab(DealerLedgerService service) => this.service = service;

    public void Draw()
    {
        var ledger = service.Ledger;

        UiHelpers.Card("Dealer Gil Reconciliation", () =>
        {
            var starting = ledger.StartingGil;
            if (UiHelpers.InputGil("Starting dealer gil", ref starting))
                ledger.StartingGil = starting;

            var ending = ledger.ActualEndingGil ?? 0;
            if (UiHelpers.InputGil("Actual ending gil", ref ending))
                ledger.ActualEndingGil = ending;
            ImGui.SameLine();
            if (ImGui.Button("Clear actual##clear-ending-gil"))
                ledger.ActualEndingGil = null;
            UiHelpers.Tooltip("Enter the gil you actually have at the end of the night. Clear this if you are not reconciling yet.");

            ImGui.Separator();
            ImGui.Text($"Expected dealer gil: {service.ExpectedDealerGil:N0} gil");
            ImGui.TextDisabled("Expected = starting gil + settled game profit/loss + tips + adjustments.");
            if (service.Difference.HasValue)
            {
                var diff = service.Difference.Value;
                ImGui.TextColored(diff == 0 ? GambaTheme.Green : diff > 0 ? GambaTheme.Gold : GambaTheme.Red,
                    $"Difference: {diff:N0} gil");
            }
            else
            {
                ImGui.TextDisabled("Difference: enter actual ending gil to calculate.");
            }
        });

        UiHelpers.Card("Night Tips", () =>
        {
            ImGui.Text($"Dealer tips total: {ledger.DealerTips:N0} gil");
            ImGui.Text($"Venue tips total: {ledger.VenueTips:N0} gil");
            ImGui.Separator();

            ImGui.TextDisabled("Record a tip for the night");
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(190f));
            UiHelpers.InputGil("Tip amount##tip-amount-add", ref tipAmountToAdd);

            ImGui.TextDisabled("Tip type");
            ImGui.RadioButton("Dealer Tip##tip-type-dealer", ref selectedTipType, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Venue Tip##tip-type-venue", ref selectedTipType, 1);

            ImGui.TextDisabled("Note (optional)");
            var noteWidth = MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(220f),
                ImGui.GetContentRegionAvail().X);
            ImGui.SetNextItemWidth(noteWidth);
            ImGui.InputText("##tip-note", ref tipNote, 160);

            var classification = selectedTipType == 0 ? TradeClassification.DealerTip : TradeClassification.VenueTip;
            if (UiHelpers.DisabledAwareButton("Add Tip##add-night-tip", tipAmountToAdd > 0, "Enter a positive tip amount first."))
            {
                service.RecordTip(tipAmountToAdd, classification, tipNote);
                tipAmountToAdd = 0;
                tipNote = string.Empty;
            }

            UiHelpers.Help("Tips are tracked separately from game profit/loss and are included in expected dealer gil for reconciliation.");
        });

        UiHelpers.Card("Session Totals", () =>
        {
            ImGui.Text($"Settled game profit/loss: {ledger.GameProfitLoss:N0} gil");
            ImGui.Text($"Dealer tips: {ledger.DealerTips:N0} gil");
            ImGui.Text($"Venue tips: {ledger.VenueTips:N0} gil");
            ImGui.Text($"Misc adjustments: {ledger.MiscAdjustments:N0} gil");
            ImGui.Separator();
            ImGui.Text($"Outstanding player banks: {service.OutstandingPlayerBanks:N0} gil");
            ImGui.Text($"Buy-ins / deposits: {ledger.TotalBuyInsDeposits:N0} gil");
            ImGui.Text($"Total cash-outs: {ledger.TotalCashOuts:N0} gil");
            ImGui.Text($"Bank/trade reconciliation P/L: {service.LiveGameProfitLoss:N0} gil");
            ImGui.TextColored(service.BankTrackingDifference == 0 ? GambaTheme.Green : GambaTheme.Gold,
                $"Bank tracking variance: {service.BankTrackingDifference:N0} gil");
            UiHelpers.Help("A non-zero bank tracking variance means recorded buy-ins, cash-outs, and outstanding player banks do not reconcile with settled hand results. It does not change expected dealer gil.");
            ImGui.Separator();
            ImGui.Text($"Total losing wagers: {ledger.TotalBets:N0} gil");
            ImGui.Text($"Total winning returns: {ledger.TotalPayouts:N0} gil");
        });
    }
}
