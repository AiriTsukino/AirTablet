using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs.SettingsTabs;

public sealed class OverlaySettingsTab
{
    private readonly Configuration config;
    private readonly BlackjackSession session;

    public OverlaySettingsTab(Configuration config, BlackjackSession session)
    {
        this.config = config;
        this.session = session;
    }

    public void Draw()
    {
        UiHelpers.Card("Visibility", () =>
        {
            var enabled = config.Overlay.Enabled;
            if (ImGui.Checkbox("Overlay enabled", ref enabled))
                config.Overlay.Enabled = enabled;

            var compact = config.Overlay.Compact;
            if (ImGui.Checkbox("Compact mode", ref compact))
                config.Overlay.Compact = compact;

            var panelColumns = Math.Clamp(config.Overlay.PlayerPanelColumns, 1, 8);
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(140f));
            if (ImGui.SliderInt("Player panels wide", ref panelColumns, 1, 8))
                config.Overlay.PlayerPanelColumns = Math.Clamp(panelColumns, 1, 8);
            UiHelpers.Help("Controls how many player panels appear per row in the normal movable Blackjack overlay. Use 2 for a 2x4 style full-party layout or 4 for a 4x2 style layout.");

            var drawn = config.Overlay.UseDrawnOverhead;
            if (ImGui.Checkbox("Use drawn overhead overlays", ref drawn))
                config.Overlay.UseDrawnOverhead = drawn;
            UiHelpers.Help("Off: one movable table overlay window that lists every current table member. On: drawn labels above matched in-world party characters, with screen-space fallback if projection fails.");

            var only = config.Overlay.ShowOnlyTableMembers;
            if (ImGui.Checkbox("Show only active table members", ref only))
                config.Overlay.ShowOnlyTableMembers = only;
        });

        UiHelpers.Card("Position / Rendering", () =>
        {
            var scale = config.Overlay.TextScale;
            if (ImGui.InputFloat("Text scale", ref scale))
                config.Overlay.TextScale = Math.Max(0.25f, scale);

            var offset = config.Overlay.VerticalOffset;
            if (ImGui.SliderFloat("Main height offset", ref offset, -1.0f, 4.0f, "%.2f"))
                config.Overlay.VerticalOffset = offset;
            UiHelpers.Help("Main vertical height used by drawn overhead overlays. Per-player adjustments below are added on top of this value.");

            var max = config.Overlay.MaxRenderDistance;
            if (ImGui.InputFloat("Max render distance", ref max))
                config.Overlay.MaxRenderDistance = Math.Max(0f, max);

            var opaque = config.Overlay.OpaqueBackground;
            if (ImGui.Checkbox("Opaque background", ref opaque))
                config.Overlay.OpaqueBackground = opaque;
            UiHelpers.Help("Makes the overlay background fully solid for maximum readability over bright or busy game elements. Turn this off to use the opacity slider below.");

            var opacity = config.Overlay.BackgroundOpacity;
            if (ImGui.SliderFloat("Background opacity", ref opacity, 0f, 1f))
                config.Overlay.BackgroundOpacity = opacity;
            UiHelpers.Help(config.Overlay.OpaqueBackground
                ? "The saved transparency level is preserved, but the opaque background option currently overrides it."
                : "Controls overlay background transparency. Higher values make the overlay easier to read over the game.");
        });

        UiHelpers.Card("Per-player height adjustments", () =>
        {
            UiHelpers.Help("Use these offsets when a specific player needs their drawn overhead overlay raised or lowered. Values are saved by Name@World and added to the main height offset.");

            var players = session.SessionPlayers
                .OrderBy(p => p.PartySlot)
                .Take(8)
                .ToList();

            if (players.Count == 0)
            {
                ImGui.TextDisabled("No current table members.");
                return;
            }

            foreach (var player in players)
            {
                var key = player.Identity.ToString();
                if (!config.Overlay.PlayerHeightOffsets.TryGetValue(key, out var playerOffset))
                    playerOffset = 0f;

                ImGui.PushID(key);
                ImGui.TextUnformatted(player.DisplayName);
                ImGui.SameLine(AirTablet.UI.TabletAppTheme.Px(230f));
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220f));
                if (ImGui.SliderFloat("##playerHeightOffset", ref playerOffset, -2.0f, 2.0f, "%.2f"))
                {
                    if (Math.Abs(playerOffset) < 0.01f)
                        config.Overlay.PlayerHeightOffsets.Remove(key);
                    else
                        config.Overlay.PlayerHeightOffsets[key] = playerOffset;
                }
                ImGui.SameLine();
                if (ImGui.Button("Reset"))
                    config.Overlay.PlayerHeightOffsets.Remove(key);
                ImGui.PopID();
            }
        });
    }
}
