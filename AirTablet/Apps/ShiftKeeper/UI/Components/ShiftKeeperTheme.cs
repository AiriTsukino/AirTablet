using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ShiftKeeper.UI.Components;

internal static class ShiftKeeperTheme
{
    private static int colorCount;
    private static int varCount;
    internal static Vector4 Purple => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.Accent : new(0.55f, 0.22f, 0.95f, 1f);
    internal static readonly Vector4 Green = new(0.36f, 0.84f, 0.48f, 1f);
    internal static readonly Vector4 Amber = new(0.95f, 0.72f, 0.28f, 1f);
    internal static Vector4 Muted => AirTablet.UI.TabletAppTheme.IsActive ? AirTablet.UI.TabletAppTheme.MutedText : new(0.62f, 0.58f, 0.72f, 1f);

    public static void Push()
    {
        if (AirTablet.UI.TabletAppTheme.IsActive)
        {
            AirTablet.UI.TabletAppTheme.Push();
            return;
        }

        colorCount = 0; varCount = 0;
        Color(ImGuiCol.Text, new Vector4(0.92f, 0.90f, 0.98f, 1f));
        Color(ImGuiCol.TextDisabled, Muted);
        Color(ImGuiCol.WindowBg, new Vector4(0.055f, 0.052f, 0.075f, 0.98f));
        Color(ImGuiCol.ChildBg, new Vector4(0.075f, 0.070f, 0.100f, 0.78f));
        Color(ImGuiCol.PopupBg, new Vector4(0.070f, 0.064f, 0.095f, 0.99f));
        Color(ImGuiCol.Border, new Vector4(0.38f, 0.20f, 0.62f, 0.65f));
        Color(ImGuiCol.FrameBg, new Vector4(0.13f, 0.12f, 0.17f, 1f));
        Color(ImGuiCol.FrameBgHovered, new Vector4(0.19f, 0.15f, 0.28f, 1f));
        Color(ImGuiCol.FrameBgActive, new Vector4(0.25f, 0.17f, 0.42f, 1f));
        Color(ImGuiCol.TitleBg, new Vector4(0.16f, 0.08f, 0.25f, 1f));
        Color(ImGuiCol.TitleBgActive, new Vector4(0.26f, 0.11f, 0.43f, 1f));
        Color(ImGuiCol.TitleBgCollapsed, new Vector4(0.10f, 0.06f, 0.16f, 1f));
        Color(ImGuiCol.ScrollbarBg, new Vector4(0.07f, 0.06f, 0.10f, 0.8f));
        Color(ImGuiCol.ScrollbarGrab, new Vector4(0.26f, 0.16f, 0.38f, 1f));
        Color(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.40f, 0.24f, 0.60f, 1f));
        Color(ImGuiCol.ScrollbarGrabActive, new Vector4(0.42f, 0.12f, 0.82f, 1f));
        Color(ImGuiCol.CheckMark, new Vector4(0.66f, 0.33f, 1f, 1f));
        Color(ImGuiCol.SliderGrab, Purple);
        Color(ImGuiCol.SliderGrabActive, new Vector4(0.66f, 0.33f, 1f, 1f));
        Color(ImGuiCol.Button, new Vector4(0.16f, 0.14f, 0.22f, 1f));
        Color(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.20f, 0.48f, 1f));
        Color(ImGuiCol.ButtonActive, new Vector4(0.45f, 0.22f, 0.78f, 1f));
        Color(ImGuiCol.Header, new Vector4(0.22f, 0.13f, 0.36f, 0.82f));
        Color(ImGuiCol.HeaderHovered, new Vector4(0.33f, 0.18f, 0.55f, 0.95f));
        Color(ImGuiCol.HeaderActive, new Vector4(0.45f, 0.22f, 0.78f, 1f));
        Color(ImGuiCol.Separator, new Vector4(0.32f, 0.18f, 0.50f, 0.70f));
        Color(ImGuiCol.Tab, new Vector4(0.11f, 0.09f, 0.15f, 1f));
        Color(ImGuiCol.TabHovered, new Vector4(0.42f, 0.20f, 0.72f, 1f));
        Color(ImGuiCol.TabActive, new Vector4(0.28f, 0.12f, 0.48f, 1f));
        Color(ImGuiCol.TableHeaderBg, new Vector4(0.19f, 0.12f, 0.30f, 1f));
        Color(ImGuiCol.TableBorderStrong, new Vector4(0.38f, 0.20f, 0.62f, 0.65f));
        Color(ImGuiCol.TableBorderLight, new Vector4(0.25f, 0.16f, 0.36f, 0.60f));

        Var(ImGuiStyleVar.WindowRounding, 8f);
        Var(ImGuiStyleVar.ChildRounding, 8f);
        Var(ImGuiStyleVar.FrameRounding, 5f);
        Var(ImGuiStyleVar.GrabRounding, 5f);
        Var(ImGuiStyleVar.TabRounding, 5f);
        Var(ImGuiStyleVar.ItemSpacing, AirTablet.UI.TabletAppTheme.Px(new Vector2(8, 7)));
        Var(ImGuiStyleVar.FramePadding, AirTablet.UI.TabletAppTheme.Px(new Vector2(8, 5)));
        Var(ImGuiStyleVar.WindowPadding, AirTablet.UI.TabletAppTheme.Px(new Vector2(12, 10)));
        Var(ImGuiStyleVar.WindowBorderSize, 1f);
    }

    public static void Pop()
    {
        if (AirTablet.UI.TabletAppTheme.IsActive)
        {
            AirTablet.UI.TabletAppTheme.Pop();
            return;
        }

        if (varCount > 0) ImGui.PopStyleVar(varCount);
        if (colorCount > 0) ImGui.PopStyleColor(colorCount);
        varCount = 0; colorCount = 0;
    }

    public static void PushKofiButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.42f, 0.15f, 0.78f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.23f, 0.96f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.10f, 0.58f, 1f));
    }

    public static void PopKofiButton() => ImGui.PopStyleColor(3);

    private static void Color(ImGuiCol key, Vector4 value) { ImGui.PushStyleColor(key, value); colorCount++; }
    private static void Var(ImGuiStyleVar key, float value) { ImGui.PushStyleVar(key, value); varCount++; }
    private static void Var(ImGuiStyleVar key, Vector2 value) { ImGui.PushStyleVar(key, value); varCount++; }
}
