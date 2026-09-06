using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;

namespace WardrobeManager;

internal sealed partial class AppearanceEditor
{
    private Vector2 selectionPopupPosition;

    private void PrepareSelectionPopup(Vector2 size, bool autoHeight = true)
        => TabletAppTheme.PrepareContainedPopup(size, selectionPopupPosition, autoHeight);

    // Explicit popups avoid ImGui's combo placement outside the tablet bounds.
    private bool BeginSelectionPopup(string id, string preview, float width, Vector2 size, bool autoHeight = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        width = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight();
        if (ImGui.Button(id, new Vector2(width, height))) ImGui.OpenPopup(id + "-options");
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(pos, pos + new Vector2(Math.Max(1, width - height), height), true);
        draw.AddText(pos + ImGui.GetStyle().FramePadding, ImGui.GetColorU32(ImGuiCol.Text), preview);
        draw.PopClipRect();
        var center = pos + new Vector2(width - height * .5f, height * .5f);
        var radius = height * .16f;
        draw.AddTriangleFilled(center + new Vector2(-radius, -radius), center + new Vector2(radius, -radius),
            center + new Vector2(0, radius), ImGui.GetColorU32(ImGuiCol.Text));
        PrepareSelectionPopup(size, autoHeight);
        return ImGui.BeginPopup(id + "-options");
    }

    private static bool FavoriteButton(bool favorite)
    {
        if (favorite) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, .78f, .22f, 1f));
        var clicked = ImGui.SmallButton(favorite ? "★" : "☆");
        if (favorite) ImGui.PopStyleColor();
        return clicked;
    }
}
