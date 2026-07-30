using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class ActiveVisitorsTab
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly QueueService queue;
    private readonly DetectionService detection;
    private readonly PersistenceService persistence;

    public ActiveVisitorsTab(Configuration config, VenueService venues, VisitorService visitors, QueueService queue, DetectionService detection, PersistenceService persistence)
    {
        this.config = config;
        this.venues = venues;
        this.visitors = visitors;
        this.queue = queue;
        this.detection = detection;
        this.persistence = persistence;
    }

    public void Draw()
    {
        if (!venues.IsVenueActive)
        {
            DrawPausedMonitorVisitors();
            return;
        }

        var venue = venues.ActiveVenue;
        var session = venue.Session;
        var activeVisitors = session.NightlyVisitors
            .Where(v => v.Present && (config.ShowBlacklistedInActiveVisitors || !venue.Blacklist.Contains(v.Key.ToString())))
            .OrderBy(v => v.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Key.World, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DrawActiveVisitorsHeader(activeVisitors.Count);
        UiHelpers.TextDisabledWrapped("Shows people currently detected in the active venue area. This follows the venue's visitor list region, so the default housing interior acts as a whole-house active player count.");

        if (activeVisitors.Count == 0)
        {
            ImGui.Spacing();
            UiHelpers.TextDisabledWrapped("No visitors are currently detected in the active venue area.");
            return;
        }

        if (ImGui.BeginTable("active-visitors-table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Visitor", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(132f));
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(112f));
            ImGui.TableHeadersRow();

            for (var i = 0; i < activeVisitors.Count; i++)
            {
                var state = activeVisitors[i];
                var key = state.Key;
                var lifetimeVisitor = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) ? visitor : null;
                var isVip = lifetimeVisitor is not null && (lifetimeVisitor.Vip || lifetimeVisitor.VipTierId != Guid.Empty);
                var vipTierName = isVip
                    ? venue.GetVipTier(lifetimeVisitor!.VipTierId)?.Name ?? venue.GetDefaultVipTier().Name
                    : null;
                var isBlacklisted = venue.Blacklist.Contains(key.ToString());
                var isUngreeted = VenueService.ContainsKey(session.Ungreeted, key);
                var isGreeted = VenueService.ContainsKey(session.Greeted, key);
                var isSkipped = VenueService.ContainsKey(session.Skipped, key);

                ImGui.PushID($"active-visitor-{i}-{key}");
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                UiHelpers.VisitorRow(key, present: true, state.ReturningThisSession, state.HereWhenArrived);

                ImGui.TableSetColumnIndex(1);
                DrawStatus(vipTierName, isBlacklisted, isUngreeted, isGreeted, isSkipped, state.HereWhenArrived);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled(state.LastSeenUtc.LocalDateTime.ToString("g"));

                ImGui.TableSetColumnIndex(3);
                DrawActionButtons(key, isVip, isBlacklisted);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawPausedMonitorVisitors()
    {
        if (!config.MonitorWhenNoVenueSelected)
        {
            UiHelpers.TextDisabledWrapped("No active venue is selected. Visitor tracking is paused. Enable monitor-only mode on the Main tab to keep visitor tracking and entry alerts running while no venue is selected.");
            return;
        }

        var activeVisitors = detection.PresentKeys
            .Select(x => VisitorKey.TryParse(x, out var key) ? key : default)
            .Where(k => !string.IsNullOrWhiteSpace(k.Name) && !string.IsNullOrWhiteSpace(k.World))
            .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.World, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DrawActiveVisitorsHeader(activeVisitors.Count, showOption: false);
        UiHelpers.TextDisabledWrapped("Monitor-only mode is enabled. This tab shows people currently detected in the house or enabled custom region, and entry alerts can still fire. Greets, queueing, and auto-greetings remain paused because no venue is selected.");

        if (activeVisitors.Count == 0)
        {
            ImGui.Spacing();
            UiHelpers.TextDisabledWrapped("No visitors are currently detected in the monitored area.");
            return;
        }

        if (ImGui.BeginTable("paused-active-visitors-table", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Visitor", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < activeVisitors.Count; i++)
            {
                var key = activeVisitors[i];
                ImGui.PushID($"paused-active-visitor-{i}-{key}");
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                UiHelpers.VisitorRow(key, present: true, returning: false, hereWhenArrived: false);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted("Present");

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawActiveVisitorsHeader(int count, bool showOption = true)
    {
        var header = $"Active visitors ({count})";
        ImGui.TextUnformatted(header);

        if (showOption)
        {
            const string labelText = "Show blacklisted";
            const float rightMargin = 14f;
            var style = ImGui.GetStyle();
            var labelWidth = ImGui.CalcTextSize(labelText).X;
            var checkboxSize = ImGui.GetFrameHeight();
            var groupWidth = labelWidth + style.ItemInnerSpacing.X + checkboxSize;
            var rightAlignedX = ImGui.GetWindowContentRegionMax().X - groupWidth - rightMargin;

            ImGui.SameLine();
            if (rightAlignedX > ImGui.GetCursorPosX())
                ImGui.SetCursorPosX(rightAlignedX);

            ImGui.TextUnformatted(labelText);
            ImGui.SameLine(0f, style.ItemInnerSpacing.X);

            var showBlacklisted = config.ShowBlacklistedInActiveVisitors;
            if (ImGui.Checkbox("##active-visitors-show-blacklisted", ref showBlacklisted))
            {
                config.ShowBlacklistedInActiveVisitors = showBlacklisted;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("When enabled, blacklisted people are included in the active Visitors list and active visitor count. They are still not queued or greeted.");
        }

        var max = ImGui.GetItemRectMax();
        var windowPos = ImGui.GetWindowPos();
        var contentMin = ImGui.GetWindowContentRegionMin();
        var contentMax = ImGui.GetWindowContentRegionMax();
        var lineColor = ImGui.GetColorU32(new System.Numerics.Vector4(0.39f, 0.20f, 0.58f, 0.75f));
        ImGui.GetWindowDrawList().AddLine(
            new System.Numerics.Vector2(
                windowPos.X + contentMin.X,
                max.Y + AirTablet.UI.TabletAppTheme.Px(4f)),
            new System.Numerics.Vector2(
                windowPos.X + contentMax.X,
                max.Y + AirTablet.UI.TabletAppTheme.Px(4f)),
            lineColor);
        ImGui.Dummy(AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(0f, 8f)));
    }

    private void DrawActionButtons(VisitorKey key, bool isVip, bool isBlacklisted)
    {
        var width = ImGui.GetContentRegionAvail().X;

        if (!isBlacklisted)
            if (ImGui.Button("Greet##active-greet", new System.Numerics.Vector2(width, 0))) queue.Enqueue(key, forceStart: true);

        if (ImGui.Button(isVip ? "Un-VIP##active-vip" : "VIP##active-vip", new System.Numerics.Vector2(width, 0))) visitors.SetVip(key, !isVip);
        if (ImGui.Button(isBlacklisted ? "Unblacklist##active-blacklist" : "Blacklist##active-blacklist", new System.Numerics.Vector2(width, 0))) visitors.ToggleBlacklist(key);
    }

    private static void DrawStatus(string? vipTierName, bool isBlacklisted, bool isUngreeted, bool isGreeted, bool isSkipped, bool hereWhenArrived)
    {
        var parts = new List<string>();
        if (isBlacklisted) parts.Add("Blacklisted");
        if (!string.IsNullOrWhiteSpace(vipTierName)) parts.Add($"VIP: {vipTierName}");
        if (isUngreeted) parts.Add("Ungreeted");
        else if (isSkipped) parts.Add("Skipped");
        else if (isGreeted) parts.Add(hereWhenArrived ? "Here on arrival" : "Greeted");
        else parts.Add("Present");

        ImGui.TextUnformatted(string.Join(" • ", parts));
    }
}
