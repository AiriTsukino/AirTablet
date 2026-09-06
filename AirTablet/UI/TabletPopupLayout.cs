using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AirTablet.UI;

internal static class TabletPopupLayout
{
    // All arguments are screen pixels; caller supplies its scaled inset bounds.
    internal static void Prepare(Vector2 preferredSize, Vector2 min, Vector2 max, Vector2? preferredPosition, bool autoHeight)
    {
        var size = Vector2.Min(preferredSize, Vector2.Max(Vector2.One, max - min));
        var position = preferredPosition.HasValue
            ? Vector2.Clamp(preferredPosition.Value, min, Vector2.Max(min, max - size))
            : min + (max - min - size) * .5f;
        if (autoHeight && preferredPosition.HasValue)
            position.Y = Math.Clamp(preferredPosition.Value.Y, min.Y, Math.Max(min.Y, max.Y - Math.Min(80, size.Y)));
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(autoHeight ? new Vector2(size.X, 0) : size, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(autoHeight ? new Vector2(size.X, 0) : size,
            autoHeight ? new Vector2(size.X, Math.Min(size.Y, max.Y - position.Y)) : size);
    }
}
