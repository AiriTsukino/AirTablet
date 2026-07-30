using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RaffleManager.UI.Components;

internal static class UiHelpers
{
    public static void Header(string title, string? subtitle = null)
    {
        ImGui.TextColored(RaffleTheme.Pink, title);
        if (!string.IsNullOrWhiteSpace(subtitle)) TextMutedWrapped(subtitle);
        ImGui.Separator();
    }

    public static bool BeginCard(
        string id,
        Vector2 size = default,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, RaffleTheme.PanelBg);
        ImGui.PushStyleColor(ImGuiCol.Border, RaffleTheme.Border);
        return ImGui.BeginChild(id, size, true, flags);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    public static void TextMuted(string text) => ImGui.TextColored(RaffleTheme.Muted, text);
    public static void TextMutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, RaffleTheme.Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static string Gil(int amount) => $"{amount:N0} gil";

    public static bool InputIntGil(string label, ref int value, int step = 1000)
    {
        var changed = ImGui.InputInt(label, ref value, step, Math.Max(step * 10, step));
        if (value < 0) value = 0;
        return changed;
    }

    public static void ClippedTextWithTooltip(string text)
    {
        text ??= string.Empty;
        ImGui.TextUnformatted(text);
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(text).X > ImGui.GetItemRectSize().X)
            WrappedTooltip(text);
    }

    public static void TooltipOnHover(string text)
    {
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(text))
            WrappedTooltip(text);
    }

    public static void WrappedTooltip(string text, float wrapWidth = 420f)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(wrapWidth));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

}
