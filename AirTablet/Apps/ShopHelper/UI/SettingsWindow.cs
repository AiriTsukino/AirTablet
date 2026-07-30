using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShopHelper.Services;
using ShopHelper.UI.Components;

namespace ShopHelper.UI;

internal sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;

    public SettingsWindow(Configuration config, PersistenceService persistence)
        : base("ShopHelper Settings###ShopHelperSettings")
    {
        Size = AirTablet.UI.TabletAppTheme.Px(new Vector2(620, 420));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(560, 360)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.config = config;
        this.persistence = persistence;
    }

    public override void PreDraw() => ShopHelperTheme.Push();
    public override void PostDraw() => ShopHelperTheme.Pop();

    public override void Draw()
    {
        if (UiHelpers.BeginCard("##Defaults", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0))))
        {
            UiHelpers.Header("Amount Defaults", "Configure the starting values used by new shop rows.");

            var quantity = config.DefaultQuantity;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Default plain number", ref quantity, 1, 99))
            {
                config.DefaultQuantity = Math.Clamp(quantity, 1, 9999);
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Default quantity used when Stacks is not ticked. Valid range: 1-9999.");

            var stacks = config.DefaultStacks;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Default stacks", ref stacks, 1, 10))
            {
                config.DefaultStacks = Math.Clamp(stacks, 1, 100);
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Default amount used when Stacks is ticked. Valid range: 1-100 stacks.");

            var stackSize = config.StackSize;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Stack size", ref stackSize, 1, 10))
            {
                config.StackSize = Math.Clamp(stackSize, 1, 99);
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("How many items one stack means. Defaults to 99 and is clamped to 1-99.");

            UiHelpers.SectionTitle("Behaviour");
            var autoOpen = config.AutoOpenWithShop;
            if (ImGui.Checkbox("Open ShopHelper automatically when a shop opens", ref autoOpen))
            {
                config.AutoOpenWithShop = autoOpen;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("When enabled, ShopHelper opens itself whenever a supported FFXIV shop UI appears. Enabled by default.");

            var confirmBeforeBuying = config.ConfirmBeforeBuying;
            if (ImGui.Checkbox("Show confirmation before buying", ref confirmBeforeBuying))
            {
                config.ConfirmBeforeBuying = confirmBeforeBuying;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Shows a ShopHelper confirmation popup with total quantity and total cost before starting a purchase. Enabled by default.");

            var yes = config.AutoConfirmYesNo;
            if (ImGui.Checkbox("Auto-confirm Yes/No purchase prompts", ref yes))
            {
                config.AutoConfirmYesNo = yes;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Automatically presses Yes on SelectYesno while a ShopHelper purchase queue is running.");

        }
        UiHelpers.EndCard();
    }
}
