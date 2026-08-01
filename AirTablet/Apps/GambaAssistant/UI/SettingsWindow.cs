using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;
using GambaAssistant.UI.Tabs.SettingsTabs;

namespace GambaAssistant.UI;

public sealed class SettingsWindow : Window
{
    private readonly GeneralSettingsTab general;
    private readonly ProfileSettingsTab profiles;
    private readonly ChatTemplateSettingsTab templates;
    private readonly ProfileService profileService;
    private readonly BlackjackSession session;
    private int selectedTab;

    public SettingsWindow(Configuration config, BlackjackSession session, ProfileService profileService, PersistenceService persistence, LogService log)
        : base("GambaAssistant Settings###GambaAssistantSettingsWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.profileService = profileService;
        this.session = session;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(860, 560)), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        general = new GeneralSettingsTab(config, session);
        profiles = new ProfileSettingsTab(profileService, session);
        templates = new ChatTemplateSettingsTab(profileService, session);
    }

    public override void PreDraw() => GambaTheme.Push();
    public override void PostDraw() => GambaTheme.Pop();

    public override void Draw()
    {
        profileService.BindActiveProfileRules(session);
        DrawNavigation();
        ImGui.Dummy(AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 5f)));
        ImGui.BeginChild("##GambaSettingsContent", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar);
        DrawSettingsPanel();
        ImGui.EndChild();
    }

    private void DrawSettingsPanel()
    {
        var panelSize = ImGui.GetContentRegionAvail();

        switch (selectedTab)
        {
            case 0: UiHelpers.Panel("General", general.Draw, panelSize); break;
            case 1: UiHelpers.Panel("Venue Profiles", profiles.Draw, panelSize); break;
            case 2: UiHelpers.Panel("Blackjack Rules", profiles.DrawRules, panelSize); break;
            case 3: UiHelpers.Panel("Chat Templates", templates.Draw, panelSize); break;
        }
    }

    private void DrawNavigation()
    {
        var labels = new[] { "General", "Venue Profiles", "Blackjack Rules", "Chat Templates" };
        if (!ImGui.BeginTable(
                "##gamba-settings-nav",
                labels.Length,
                ImGuiTableFlags.SizingStretchSame))
        {
            return;
        }

        ImGui.TableNextRow();
        for (var index = 0; index < labels.Length; index++)
        {
            ImGui.TableNextColumn();
            if (UiHelpers.VerticalNavItem(
                    $"{labels[index]}##gamba-settings-{index}",
                    selectedTab == index,
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                selectedTab = index;
            }
        }
        ImGui.EndTable();
    }

}
