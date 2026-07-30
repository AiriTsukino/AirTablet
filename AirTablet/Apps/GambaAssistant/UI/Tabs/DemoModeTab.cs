using Dalamud.Bindings.ImGui;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class DemoModeTab
{
    private readonly Configuration config;
    private readonly DemoModeService demo;
    private readonly LogService log;

    public DemoModeTab(Configuration config, DemoModeService demo, LogService log)
    {
        this.config = config;
        this.demo = demo;
        this.log = log;
    }

    public void Draw()
    {
        UiHelpers.InfoBox("Isolated Test Table", "Demo mode uses simulated party members, simulated banks, and internal dice results. It never sends real party chat and does not affect live banks, trade monitor, exports, or real session history.");

        UiHelpers.Card("Demo Controls", () =>
        {
            if (ImGui.Button("Start Demo"))
            {
                config.DemoModeEnabled = true;
                demo.StartDemo();
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop Demo"))
            {
                config.DemoModeEnabled = false;
                demo.StopDemo();
            }

            ImGui.TextDisabled(config.DemoModeEnabled ? "Demo mode is active." : "Demo mode is inactive.");
        });
    }
}
