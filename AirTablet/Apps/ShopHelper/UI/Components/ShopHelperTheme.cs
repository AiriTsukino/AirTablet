using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ShopHelper.UI.Components;

internal static class ShopHelperTheme
{
    private static int styleColorCount;
    private static int styleVarCount;

    internal static Vector4 Gold => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.AccentHover : new(0.96f, 0.78f, 0.25f, 1f);
    internal static Vector4 Purple => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.Accent : new(0.55f, 0.22f, 0.95f, 1f);
    internal static Vector4 PurpleHovered => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.AccentHover : new(0.66f, 0.33f, 1.00f, 1f);
    internal static Vector4 PurpleActive => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.Accent : new(0.42f, 0.12f, 0.82f, 1f);
    internal static Vector4 DarkBg => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.Surface : new(0.055f, 0.052f, 0.075f, 0.98f);
    internal static Vector4 PanelBg => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.SurfaceRaised : new(0.075f, 0.055f, 0.115f, 0.96f);
    internal static Vector4 FrameBg => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.SurfaceRaised : new(0.13f, 0.12f, 0.17f, 1f);
    internal static Vector4 FrameHovered => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.AccentHover : new(0.19f, 0.15f, 0.28f, 1f);
    internal static Vector4 FrameActive => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.Accent : new(0.25f, 0.17f, 0.42f, 1f);
    internal static Vector4 Border => AirTablet.UI.TabletAppTheme.IsActive ? WithAlpha(AirTablet.UI.TabletAppTheme.Accent, 0.65f) : new(0.38f, 0.20f, 0.62f, 0.65f);
    internal static Vector4 Text => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.Text : new(0.92f, 0.90f, 0.98f, 1f);
    internal static Vector4 Muted => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.MutedText : new(0.66f, 0.58f, 0.74f, 1f);
    internal static readonly Vector4 Green = new(0.48f, 0.83f, 0.62f, 1f);
    internal static readonly Vector4 Red = new(0.86f, 0.32f, 0.42f, 1f);

    public static void Push()
    {
        if (AirTablet.UI.TabletAppTheme.IsActive)
        {
            AirTablet.UI.TabletAppTheme.Push();
            return;
        }

        styleColorCount = 0;
        styleVarCount = 0;

        PushColor(ImGuiCol.Text, Text);
        PushColor(ImGuiCol.TextDisabled, Muted);
        PushColor(ImGuiCol.WindowBg, DarkBg);
        PushColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.070f, 0.100f, 0.78f));
        PushColor(ImGuiCol.PopupBg, new Vector4(0.070f, 0.064f, 0.095f, 0.99f));
        PushColor(ImGuiCol.Border, Border);
        PushColor(ImGuiCol.FrameBg, FrameBg);
        PushColor(ImGuiCol.FrameBgHovered, FrameHovered);
        PushColor(ImGuiCol.FrameBgActive, FrameActive);
        PushColor(ImGuiCol.TitleBg, new Vector4(0.16f, 0.08f, 0.25f, 1f));
        PushColor(ImGuiCol.TitleBgActive, new Vector4(0.26f, 0.11f, 0.43f, 1f));
        PushColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.10f, 0.06f, 0.16f, 1f));
        PushColor(ImGuiCol.MenuBarBg, PanelBg);
        PushColor(ImGuiCol.ScrollbarBg, new Vector4(0.07f, 0.06f, 0.10f, 0.8f));
        PushColor(ImGuiCol.ScrollbarGrab, new Vector4(0.26f, 0.16f, 0.38f, 1f));
        PushColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.40f, 0.24f, 0.60f, 1f));
        PushColor(ImGuiCol.ScrollbarGrabActive, PurpleActive);
        PushColor(ImGuiCol.CheckMark, PurpleHovered);
        PushColor(ImGuiCol.SliderGrab, Purple);
        PushColor(ImGuiCol.SliderGrabActive, PurpleHovered);
        PushColor(ImGuiCol.Button, new Vector4(0.17f, 0.12f, 0.25f, 1f));
        PushColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.20f, 0.48f, 1f));
        PushColor(ImGuiCol.ButtonActive, PurpleActive);
        PushColor(ImGuiCol.Header, new Vector4(0.22f, 0.13f, 0.36f, 0.82f));
        PushColor(ImGuiCol.HeaderHovered, new Vector4(0.33f, 0.18f, 0.55f, 0.95f));
        PushColor(ImGuiCol.HeaderActive, PurpleActive);
        PushColor(ImGuiCol.Separator, new Vector4(0.32f, 0.18f, 0.50f, 0.70f));
        PushColor(ImGuiCol.SeparatorHovered, PurpleHovered);
        PushColor(ImGuiCol.SeparatorActive, PurpleActive);
        PushColor(ImGuiCol.ResizeGrip, new Vector4(0.40f, 0.20f, 0.70f, 0.35f));
        PushColor(ImGuiCol.ResizeGripHovered, new Vector4(0.55f, 0.28f, 0.95f, 0.70f));
        PushColor(ImGuiCol.ResizeGripActive, Purple);
        PushColor(ImGuiCol.Tab, new Vector4(0.11f, 0.09f, 0.15f, 1f));
        PushColor(ImGuiCol.TabHovered, new Vector4(0.42f, 0.20f, 0.72f, 1f));
        PushColor(ImGuiCol.TabActive, new Vector4(0.28f, 0.12f, 0.48f, 1f));
        PushColor(ImGuiCol.TableHeaderBg, new Vector4(0.19f, 0.12f, 0.30f, 1f));
        PushColor(ImGuiCol.TableBorderStrong, Border);
        PushColor(ImGuiCol.TableBorderLight, new Vector4(0.25f, 0.16f, 0.36f, 0.60f));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, AirTablet.UI.TabletAppTheme.Px(8f)); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, AirTablet.UI.TabletAppTheme.Px(8f)); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, AirTablet.UI.TabletAppTheme.Px(5f)); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, AirTablet.UI.TabletAppTheme.Px(5f)); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, AirTablet.UI.TabletAppTheme.Px(5f)); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, AirTablet.UI.TabletAppTheme.Px(new Vector2(8, 7))); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, AirTablet.UI.TabletAppTheme.Px(new Vector2(8, 5))); styleVarCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, AirTablet.UI.TabletAppTheme.Px(1f)); styleVarCount++;
    }

    public static void Pop()
    {
        if (AirTablet.UI.TabletAppTheme.IsActive)
        {
            AirTablet.UI.TabletAppTheme.Pop();
            return;
        }

        if (styleVarCount > 0) ImGui.PopStyleVar(styleVarCount);
        if (styleColorCount > 0) ImGui.PopStyleColor(styleColorCount);
        styleVarCount = 0;
        styleColorCount = 0;
    }

    public static void PushKofiButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.42f, 0.15f, 0.78f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.23f, 0.96f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.10f, 0.58f, 1f));
    }

    public static void PopKofiButton() => ImGui.PopStyleColor(3);


    public static void PushHighContrastInput()
    {
        if (AirTablet.UI.TabletAppTheme.IsActive)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, AirTablet.UI.TabletAppTheme.SurfaceRaised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, WithAlpha(AirTablet.UI.TabletAppTheme.AccentHover, 0.52f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, WithAlpha(AirTablet.UI.TabletAppTheme.Accent, 0.72f));
            ImGui.PushStyleColor(ImGuiCol.Border, WithAlpha(AirTablet.UI.TabletAppTheme.AccentHover, 0.88f));
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.18f, 0.13f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.28f, 0.18f, 0.42f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.34f, 0.22f, 0.52f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.58f, 0.34f, 0.88f, 0.88f));
    }

    public static void PopHighContrastInput() => ImGui.PopStyleColor(4);

    private static void PushColor(ImGuiCol col, Vector4 color)
    {
        ImGui.PushStyleColor(col, color);
        styleColorCount++;
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) =>
        new(color.X, color.Y, color.Z, alpha);
}
