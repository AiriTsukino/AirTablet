using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ShiftKeeper.UI.Components;

internal static class UiHelpers
{
    public static void Help(string text)
    {
        if (!ImGui.IsItemHovered()) return;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, AirTablet.UI.TabletAppTheme.Px(new Vector2(12, 10)));
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(340f));
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopStyleVar();
    }

    public static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.AccentHover
                : new Vector4(0.72f, 0.65f, 0.95f, 1f),
            title);
        ImGui.Separator();
    }

    public static void Status(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
