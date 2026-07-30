using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GambaAssistant.UI.Components;

internal static class GambaTheme
{
    private static int styleColorCount;
    private static int styleVarCount;

    private static bool UseTabletTheme => AirTablet.UI.TabletAppTheme.IsActive || AirTablet.UI.TabletAppTheme.HasRememberedTheme;
    internal static Vector4 Purple => UseTabletTheme ? AirTablet.UI.TabletAppTheme.RememberedAccent : new(0.55f, 0.22f, 0.95f, 1f);
    internal static Vector4 PurpleHovered => UseTabletTheme ? AirTablet.UI.TabletAppTheme.RememberedAccentHover : new(0.66f, 0.33f, 1.00f, 1f);
    internal static Vector4 PurpleActive => UseTabletTheme ? AirTablet.UI.TabletAppTheme.RememberedAccent : new(0.42f, 0.12f, 0.82f, 1f);
    internal static Vector4 DarkBg => UseTabletTheme ? AirTablet.UI.TabletAppTheme.RememberedSurface : new(0.055f, 0.052f, 0.075f, 0.98f);
    internal static Vector4 PanelBg => UseTabletTheme ? AirTablet.UI.TabletAppTheme.RememberedSurfaceRaised : new(0.095f, 0.088f, 0.125f, 0.96f);
    internal static Vector4 FrameBg => UseTabletTheme ? AirTablet.UI.TabletAppTheme.RememberedSurfaceRaised : new(0.13f, 0.12f, 0.17f, 1f);
    internal static Vector4 FrameHovered => UseTabletTheme ? Mix(FrameBg, PurpleHovered, 0.34f) : new(0.19f, 0.15f, 0.28f, 1f);
    internal static Vector4 FrameActive => UseTabletTheme ? Mix(FrameBg, Purple, 0.52f) : new(0.25f, 0.17f, 0.42f, 1f);
    internal static Vector4 Border => UseTabletTheme ? WithAlpha(Purple, 0.65f) : new(0.38f, 0.20f, 0.62f, 0.65f);
    internal static Vector4 Text => UseTabletTheme ? AirTablet.UI.TabletAppTheme.Text : new(0.92f, 0.90f, 0.98f, 1f);
    internal static Vector4 MutedText => UseTabletTheme ? AirTablet.UI.TabletAppTheme.MutedText : new(0.62f, 0.58f, 0.72f, 1f);

    // Keep GambaAssistant-specific semantic colors available for gameplay/status UI.
    internal static Vector4 Gold => UseTabletTheme ? PurpleHovered : new(0.75f, 0.85f, 1f, 1f);
    internal static readonly Vector4 Green = new(0.20f, 0.70f, 0.42f, 1f);
    internal static readonly Vector4 Red = new(0.85f, 0.22f, 0.24f, 1f);

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
        PushColor(ImGuiCol.TextDisabled, MutedText);
        PushColor(ImGuiCol.WindowBg, DarkBg);
        PushColor(ImGuiCol.ChildBg, WithAlpha(PanelBg, 0.78f));
        PushColor(ImGuiCol.PopupBg, WithAlpha(DarkBg, 0.99f));
        PushColor(ImGuiCol.Border, Border);
        PushColor(ImGuiCol.FrameBg, FrameBg);
        PushColor(ImGuiCol.FrameBgHovered, FrameHovered);
        PushColor(ImGuiCol.FrameBgActive, FrameActive);
        PushColor(ImGuiCol.TitleBg, Mix(DarkBg, Purple, 0.18f));
        PushColor(ImGuiCol.TitleBgActive, Mix(DarkBg, Purple, 0.34f));
        PushColor(ImGuiCol.TitleBgCollapsed, Mix(DarkBg, Purple, 0.10f));
        PushColor(ImGuiCol.MenuBarBg, PanelBg);
        PushColor(ImGuiCol.ScrollbarBg, WithAlpha(DarkBg, 0.8f));
        PushColor(ImGuiCol.ScrollbarGrab, Mix(PanelBg, Purple, 0.38f));
        PushColor(ImGuiCol.ScrollbarGrabHovered, Mix(PanelBg, PurpleHovered, 0.58f));
        PushColor(ImGuiCol.ScrollbarGrabActive, PurpleActive);
        PushColor(ImGuiCol.CheckMark, PurpleHovered);
        PushColor(ImGuiCol.SliderGrab, Purple);
        PushColor(ImGuiCol.SliderGrabActive, PurpleHovered);
        PushColor(ImGuiCol.Button, FrameBg);
        PushColor(ImGuiCol.ButtonHovered, FrameHovered);
        PushColor(ImGuiCol.ButtonActive, FrameActive);
        PushColor(ImGuiCol.Header, WithAlpha(Mix(PanelBg, Purple, 0.28f), 0.82f));
        PushColor(ImGuiCol.HeaderHovered, WithAlpha(Mix(PanelBg, PurpleHovered, 0.45f), 0.95f));
        PushColor(ImGuiCol.HeaderActive, FrameActive);
        PushColor(ImGuiCol.Separator, WithAlpha(Purple, 0.55f));
        PushColor(ImGuiCol.SeparatorHovered, PurpleHovered);
        PushColor(ImGuiCol.SeparatorActive, PurpleActive);
        PushColor(ImGuiCol.ResizeGrip, WithAlpha(Purple, 0.35f));
        PushColor(ImGuiCol.ResizeGripHovered, WithAlpha(PurpleHovered, 0.70f));
        PushColor(ImGuiCol.ResizeGripActive, Purple);
        PushColor(ImGuiCol.Tab, Mix(DarkBg, PanelBg, 0.65f));
        PushColor(ImGuiCol.TabHovered, Mix(PanelBg, PurpleHovered, 0.48f));
        PushColor(ImGuiCol.TabActive, Mix(PanelBg, Purple, 0.38f));
        PushColor(ImGuiCol.TabUnfocused, DarkBg);
        PushColor(ImGuiCol.TabUnfocusedActive, Mix(DarkBg, Purple, 0.22f));
        PushColor(ImGuiCol.TableHeaderBg, Mix(PanelBg, Purple, 0.22f));
        PushColor(ImGuiCol.TableBorderStrong, Border);
        PushColor(ImGuiCol.TableBorderLight, WithAlpha(Purple, 0.32f));

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
        // Keep this independent of PushColor/styleColorCount because the support button
        // is popped separately from the plugin-scoped window theme.
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.42f, 0.15f, 0.78f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.23f, 0.96f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.10f, 0.58f, 1f));
    }

    public static void PopKofiButton() => ImGui.PopStyleColor(3);

    private static void PushColor(ImGuiCol col, Vector4 color)
    {
        ImGui.PushStyleColor(col, color);
        styleColorCount++;
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) =>
        new(color.X, color.Y, color.Z, alpha);

    private static Vector4 Mix(Vector4 from, Vector4 to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Vector4(
            from.X + (to.X - from.X) * amount,
            from.Y + (to.Y - from.Y) * amount,
            from.Z + (to.Z - from.Z) * amount,
            from.W + (to.W - from.W) * amount);
    }
}
