using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AirTablet.UI;

internal static class TabletSeparator
{
    // Header separators span the current child viewport, not a preceding table
    // column or toolbar cursor. Keep their drawing clipped to that child only.
    public static unsafe void Draw()
    {
        var context = ImGui.GetCurrentContext();
        var window = context.CurrentWindow;
        var table = context.CurrentTable;
        // A table in a parent window does not constrain a child panel's line.
        // Keep native column sizing only when this window owns the table.
        if ((table.Handle != null && table.InnerWindow.Handle == window.Handle) || ImGui.GetColumnsCount() > 1)
        { ImGui.Separator(); return; }
        // ContentRegionRect/WorkRect are layout state, not panel bounds: tables
        // and fitted toolbars can leave a narrower extent there. InnerRect is
        // the real viewport (excluding scrollbars), with this window's padding.
        var left = Math.Max(window.InnerRect.Min.X + window.WindowPadding.X, window.InnerClipRect.Min.X);
        var right = Math.Min(window.InnerRect.Max.X - window.WindowPadding.X, window.InnerClipRect.Max.X);
        var y = ImGui.GetCursorScreenPos().Y;
        var draw = ImGui.GetWindowDrawList();
        if (right > left)
        {
            draw.PushClipRect(new Vector2(left, window.InnerClipRect.Min.Y), new Vector2(right, window.InnerClipRect.Max.Y), false);
            try { draw.AddLine(new Vector2(left, y + .5f), new Vector2(right, y + .5f), ImGui.GetColorU32(ImGuiCol.Separator), 1f); }
            finally { draw.PopClipRect(); }
        }
        ImGui.SetCursorScreenPos(new Vector2(left, y));
        ImGui.Dummy(new Vector2(Math.Max(1, right - left), 1));
    }
}
