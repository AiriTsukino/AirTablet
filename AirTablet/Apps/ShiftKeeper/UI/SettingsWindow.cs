using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShiftKeeper.UI.Components;

namespace ShiftKeeper.UI;

public sealed class SettingsWindow : Window
{
    private readonly MainWindow content;

    public SettingsWindow(MainWindow content)
        : base("ShiftKeeper Settings###ShiftKeeperSettingsWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.content = content;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(820, 570)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw() => ShiftKeeperTheme.Push();
    public override void PostDraw() => ShiftKeeperTheme.Pop();
    public override void Draw() => content.DrawSettings();
}
