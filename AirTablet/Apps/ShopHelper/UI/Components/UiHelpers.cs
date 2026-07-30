using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ShopHelper.UI.Components;

internal static class UiHelpers
{
    public static void Header(string title, string? subtitle = null)
    {
        ImGui.TextColored(ShopHelperTheme.Gold, title);
        if (!string.IsNullOrWhiteSpace(subtitle))
            TextMuted(subtitle);
        ImGui.Separator();
    }

    public static void SectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(ShopHelperTheme.Gold, title);
        ImGui.Separator();
    }

    public static bool BeginCard(
        string id,
        Vector2 size = default,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ShopHelperTheme.PanelBg);
        ImGui.PushStyleColor(ImGuiCol.Border, ShopHelperTheme.Border);
        return ImGui.BeginChild(id, size, true, flags);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    public static void TextMuted(string text) => ImGui.TextColored(ShopHelperTheme.Muted, text);

    public static void TextWrappedMuted(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ShopHelperTheme.Muted, text);
        ImGui.PopTextWrapPos();
    }

    public static void TooltipOnHover(string text, float wrapWidth = 420f)
    {
        if (!ImGui.IsItemHovered() || string.IsNullOrWhiteSpace(text)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() + Math.Clamp(
                AirTablet.UI.TabletAppTheme.Px(wrapWidth),
                AirTablet.UI.TabletAppTheme.Px(220f),
                AirTablet.UI.TabletAppTheme.Px(520f)));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

}
