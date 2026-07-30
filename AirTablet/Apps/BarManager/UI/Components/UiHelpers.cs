using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace BarManager.UI.Components;

internal static class UiHelpers
{
    public static void Header(string title, string? subtitle = null)
    {
        ImGui.TextColored(BarManagerTheme.Gold, title);
        if (!string.IsNullOrWhiteSpace(subtitle))
            TextMuted(subtitle);
        ImGui.Separator();
    }

    public static void SectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(BarManagerTheme.Gold, title);
        ImGui.Separator();
    }

    public static bool BeginCard(
        string id,
        Vector2 size = default,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BarManagerTheme.PanelBg);
        ImGui.PushStyleColor(ImGuiCol.Border, BarManagerTheme.Border);
        return ImGui.BeginChild(id, size, true, flags);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    public static void TextMuted(string text) => ImGui.TextColored(BarManagerTheme.Muted, text);

    public static void TextWrappedMuted(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(BarManagerTheme.Muted, text);
        ImGui.PopTextWrapPos();
    }

    public static string Gil(int amount) => $"{amount:N0} gil";

    public static bool InputIntGil(string label, ref int value, int step = 1000)
    {
        var changed = ImGui.InputInt(label, ref value, step, Math.Max(step * 10, step));
        if (value < 0) value = 0;
        return changed;
    }

    public static void TooltipOnHover(string text, float wrapWidth = 420f)
    {
        if (!ImGui.IsItemHovered() || string.IsNullOrWhiteSpace(text))
            return;

        DrawWrappedTooltip(text, wrapWidth);
    }

    public static void DrawWrappedTooltip(string text, float wrapWidth = 420f)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        ImGui.BeginTooltip();
        var safeWrapWidth = Math.Clamp(
            AirTablet.UI.TabletAppTheme.Px(wrapWidth),
            AirTablet.UI.TabletAppTheme.Px(220f),
            AirTablet.UI.TabletAppTheme.Px(520f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + safeWrapWidth);
        ImGui.TextUnformatted(WrapTooltipText(text));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void HelpMarker(string text, float wrapWidth = 420f)
    {
        ImGui.TextDisabled("(?)");
        TooltipOnHover(text, wrapWidth);
    }

    public static bool CheckboxWithHelp(string label, ref bool value, string tooltip, float wrapWidth = 420f)
    {
        var changed = ImGui.Checkbox(label, ref value);
        ImGui.SameLine();
        HelpMarker(tooltip, wrapWidth);
        return changed;
    }

    private static string WrapTooltipText(string text, int characterLimit = 200)
    {
        if (text.Length <= characterLimit || text.Contains('\n'))
            return text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > characterLimit)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : $"{current} {word}";
            }
        }

        if (current.Length > 0)
            lines.Add(current);

        return string.Join(Environment.NewLine, lines);
    }

}
