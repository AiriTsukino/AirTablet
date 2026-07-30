using Dalamud.Bindings.ImGui;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs.SettingsTabs;

public sealed class AdvancedSettingsTab
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly LogService log;

    public AdvancedSettingsTab(Configuration config, PersistenceService persistence, LogService log)
    {
        this.config = config;
        this.persistence = persistence;
        this.log = log;
    }

    public void Draw()
    {
        UiHelpers.InfoBox("Conservative Advanced Controls", "Advanced controls are intentionally conservative for trade/gil APIs. Use manual reconciliation when automatic detection is unavailable or uncertain.");

        UiHelpers.Card("Persistence", () =>
        {
            if (ImGui.Button("Save Config Now"))
            {
                persistence.SaveNow();
                log.Add(LogCategory.Info, "Configuration saved.");
            }

            ImGui.TextWrapped($"Config root: {persistence.ConfigRoot}");
        });
    }
}
