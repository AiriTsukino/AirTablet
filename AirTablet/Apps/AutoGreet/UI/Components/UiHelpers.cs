using System.Numerics;
using System.Text;
using AutoGreet.Models;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Components;

internal static class UiHelpers
{
    public static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.AccentHover
                : new Vector4(0.75f, 0.85f, 1f, 1f),
            title);
        ImGui.Separator();
    }

    public static void SubSection(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.AccentHover
                : new Vector4(0.72f, 0.65f, 0.95f, 1f),
            title);
    }

    public static void TooltipOnHover(string text, int maxLineLength = 80)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(WrapTooltip(text, maxLineLength));
    }

    public static string WrapTooltip(string text, int maxLineLength = 80)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            var currentLength = 0;
            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (currentLength > 0 && currentLength + 1 + word.Length > maxLineLength)
                {
                    builder.AppendLine();
                    currentLength = 0;
                }

                if (currentLength > 0)
                {
                    builder.Append(' ');
                    currentLength++;
                }

                builder.Append(word);
                currentLength += word.Length;
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static Vector2 GetPopupPositionNearMouse(Vector2 approximateSize)
    {
        var padding = AirTablet.UI.TabletAppTheme.Px(new Vector2(12f, 12f));
        var pos = ImGui.GetMousePos() + padding;
        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos + AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f));
        var max = viewport.WorkPos + viewport.WorkSize - approximateSize - AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f));

        if (max.X < min.X) max.X = min.X;
        if (max.Y < min.Y) max.Y = min.Y;

        return new Vector2(Math.Clamp(pos.X, min.X, max.X), Math.Clamp(pos.Y, min.Y, max.Y));
    }

    public static void SetNextPopupPositionNearMouse(Vector2 anchor, Vector2 approximateSize)
    {
        if (anchor == Vector2.Zero)
            anchor = GetPopupPositionNearMouse(approximateSize);

        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos + AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f));
        var max = viewport.WorkPos + viewport.WorkSize - approximateSize - AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f));

        if (max.X < min.X) max.X = min.X;
        if (max.Y < min.Y) max.Y = min.Y;

        var clamped = new Vector2(Math.Clamp(anchor.X, min.X, max.X), Math.Clamp(anchor.Y, min.Y, max.Y));
        ImGui.SetNextWindowPos(clamped, ImGuiCond.Appearing, Vector2.Zero);
    }

    public static void TextDisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void VisitorRow(VisitorKey key, bool present, bool returning, bool hereWhenArrived = false)
    {
        var color = present ? new Vector4(0.70f, 1f, 0.70f, 1f) : new Vector4(0.65f, 0.65f, 0.65f, 1f);
        ImGui.TextColored(color, key.Display);
        if (returning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("returning");
        }

        if (hereWhenArrived)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("[Here When Arrived]");
        }
    }
}
