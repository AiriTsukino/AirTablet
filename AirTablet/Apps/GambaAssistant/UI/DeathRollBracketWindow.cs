using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI;

public sealed class DeathRollBracketWindow : Window
{
    private readonly MainWindow mainWindow;

    public DeathRollBracketWindow(MainWindow mainWindow)
        : base("DRT Tournament Bracket###GambaAssistantDrtBracketWindow", ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.mainWindow = mainWindow;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(760, 520)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw() => GambaTheme.Push();

    public override void PostDraw() => GambaTheme.Pop();

    public override void Draw()
    {
        mainWindow.DrawDrtBracketWindow();
    }
}
