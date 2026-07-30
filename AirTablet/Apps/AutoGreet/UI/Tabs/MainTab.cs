using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class MainTab
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly QueueService queue;
    private readonly DetectionService detection;
    private readonly PersistenceService persistence;
    private readonly Action openSettings;
    private System.Numerics.Vector2 resetSessionPopupAnchor = System.Numerics.Vector2.Zero;
    private System.Numerics.Vector2 manualScanPopupAnchor = System.Numerics.Vector2.Zero;

    public MainTab(Configuration config, VenueService venues, VisitorService visitors, QueueService queue, DetectionService detection, PersistenceService persistence, Action openSettings)
    {
        this.config = config;
        this.venues = venues;
        this.visitors = visitors;
        this.queue = queue;
        this.detection = detection;
        this.persistence = persistence;
        this.openSettings = openSettings;
    }

    public void Draw()
    {
        DrawVenueToolbar();

        if (!venues.IsVenueActive)
        {
            if (config.AutoGreetEnabled)
            {
                config.AutoGreetEnabled = false;
                persistence.SaveNow();
            }

            UiHelpers.TextDisabledWrapped("No active venue is selected. Greeting lists, queueing, and auto-greetings are paused until you select a venue again.");
            var monitor = config.MonitorWhenNoVenueSelected;
            if (ImGui.Checkbox("Keep entry alerts and active Visitors tab enabled while paused", ref monitor))
            {
                config.MonitorWhenNoVenueSelected = monitor;
                detection.ClearPresenceCache();
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("When enabled, None acts like a monitor-only mode: AutoGreet will still scan the current house or custom region for entry alerts and the Visitors tab, but it will not populate Greets, queue anyone, or run greetings.");
            if (!config.MonitorWhenNoVenueSelected)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Detection status: {detection.LastStatus}");
            }

            return;
        }

        var venue = venues.ActiveVenue;
        var session = venue.Session;

        UiHelpers.SetNextPopupPositionNearMouse(
            manualScanPopupAnchor,
            AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(500f, 190f)));
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Manual scan?##main-manual-scan-popup",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("This will add everyone currently in the house or custom region to the ungreeted list. Use this when you intentionally want to greet people who were already present when you arrived.");
            ImGui.Separator();
            if (ImGui.Button(
                    "Manual Scan##main-confirm-manual-scan",
                    AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(140, 0))))
            {
                var count = visitors.ImportCurrentVisitorsForGreeting(detection.GetCurrentVisibleVisitors());
                if (count > 0 && config.AutoGreetEnabled)
                    queue.EnqueueEligibleUngreeted(true);
                manualScanPopupAnchor = System.Numerics.Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            ImGui.SameLine();
            if (ImGui.Button(
                    "Cancel##main-cancel-manual-scan",
                    AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(100, 0))))
            {
                manualScanPopupAnchor = System.Numerics.Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }

        UiHelpers.SetNextPopupPositionNearMouse(
            resetSessionPopupAnchor,
            AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(460f, 190f)));
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Reset session?##main-reset-session-popup",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Reset the current venue session? This clears the ungreeted, greeted, skipped, nightly visitor, and queue lists. Lifetime visitor history, VIPs, blacklist, venues, and macros are kept.");
            ImGui.Separator();
            if (ImGui.Button(
                    "Reset Session##main-confirm-reset",
                    AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(140, 0))))
            {
                visitors.ResetSession();
                resetSessionPopupAnchor = System.Numerics.Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            ImGui.SameLine();
            if (ImGui.Button(
                    "Cancel##main-cancel-reset",
                    AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(100, 0))))
            {
                resetSessionPopupAnchor = System.Numerics.Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }

        DrawGreetingWorkspace(venue);
        DrawSessionSummary(venue, session);

        if (ImGui.BeginTable("main-lists-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Ungreeted");
            ImGui.TableSetupColumn("Greeted");
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawUngreeted(session);

            ImGui.TableSetColumnIndex(1);
            DrawGreeted(session);

            ImGui.EndTable();
        }
    }

    private void DrawVenueSelector()
    {
        var activeVenue = venues.ActiveVenueOrNull;
        var preview = activeVenue?.Name ?? "None - AutoGreet paused";
        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##main-active-venue", preview)) return;

        if (ImGui.Selectable("None - pause AutoGreet##main-venue-none", activeVenue is null))
        {
            venues.SwitchVenue(Guid.Empty);
            config.AutoGreetEnabled = false;
            detection.ClearPresenceCache();
            persistence.SaveNow();
        }
        if (activeVenue is null)
            ImGui.SetItemDefaultFocus();

        ImGui.Separator();

        foreach (var venue in venues.Venues.ToArray())
        {
            var selected = activeVenue is not null && venue.Id == activeVenue.Id;
            if (ImGui.Selectable($"{venue.Name}##main-venue-{venue.Id}", selected))
            {
                venues.SwitchVenue(venue.Id);
                EnsureActiveMacroDefaults(venues.ActiveVenue);
                persistence.SaveNow();
                if (config.AutoGreetEnabled)
                    queue.EnqueueEligibleUngreeted(true);
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawGreetingProfileSelector(VenueProfile venue)
    {
        var activeProfile = venues.GetGreetingProfileForVenue(venue);
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(280));
        if (!ImGui.BeginCombo("Greeting profile##main-active-greeting-profile", activeProfile.Name)) return;

        foreach (var item in venues.AllGreetingProfiles.ToArray())
        {
            var selected = item.Profile.Id == venue.ActiveGreetingProfileId;
            var label = item.Venue.Id == venue.Id
                ? item.Profile.Name
                : $"{item.Profile.Name}  ({item.Venue.Name})";

            if (ImGui.Selectable($"{label}##main-greeting-profile-{item.Profile.Id}", selected))
            {
                venue.ActiveGreetingProfileId = item.Profile.Id;
                EnsureActiveMacroDefaults(venue);
                persistence.SaveNow();
                if (config.AutoGreetEnabled)
                    queue.EnqueueEligibleUngreeted(true);
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawMacroSelector(VenueProfile venue, GreetingCategory category, string label)
    {
        var profile = venues.GetGreetingProfileForVenue(venue);
        var macros = profile.Macros
            .Where(m => m.Enabled && m.Category == category)
            .ToList();

        var activeId = venue.GetActiveMacroId(category);
        if (activeId != Guid.Empty && macros.All(m => m.Id != activeId))
        {
            activeId = macros.FirstOrDefault()?.Id ?? Guid.Empty;
            venue.SetActiveMacroId(category, activeId);
            persistence.SaveNow();
        }

        var preview = activeId == Guid.Empty
            ? "None configured"
            : macros.FirstOrDefault(m => m.Id == activeId)?.Name ?? "None configured";

        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(280));
        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.Selectable("None", activeId == Guid.Empty))
            {
                venue.SetActiveMacroId(category, Guid.Empty);
                persistence.SaveNow();
            }

            foreach (var macro in macros)
            {
                var selected = macro.Id == activeId;
                if (ImGui.Selectable($"{macro.Name}##{category}-{macro.Id}", selected))
                {
                    venue.SetActiveMacroId(category, macro.Id);
                    persistence.SaveNow();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private void DrawVipMacroSelector(VenueProfile venue)
    {
        var profile = venues.GetGreetingProfileForVenue(venue);
        var macros = profile.Macros
            .Where(m => m.Enabled && m.Category == GreetingCategory.Vip)
            .ToList();
        var tiers = venue.VipTiers.ToArray();
        var configuredCount = tiers.Count(tier =>
            venue.GetActiveVipMacroId(tier.Id) is var macroId
            && macroId != Guid.Empty
            && macros.Any(m => m.Id == macroId));

        string preview;
        if (tiers.Length == 1)
        {
            var selectedId = venue.GetActiveVipMacroId(tiers[0].Id);
            preview = macros.FirstOrDefault(m => m.Id == selectedId)?.Name ?? "None configured";
        }
        else
        {
            preview = $"{configuredCount} of {tiers.Length} tiers configured";
        }

        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(280));
        var comboOpen = ImGui.BeginCombo("Active VIP macros", preview);
        UiHelpers.TooltipOnHover("Choose one active VIP macro for each tier. The selector stays open so multiple tiers can be configured together.");
        if (!comboOpen)
            return;

        foreach (var tier in tiers)
        {
            ImGui.PushID($"active-vip-tier-{tier.Id}");
            ImGui.TextDisabled(tier.Id == venue.DefaultVipTierId ? $"{tier.Name} (default)" : tier.Name);
            ImGui.Indent();

            var selectedId = venue.GetActiveVipMacroId(tier.Id);
            if (macros.Count == 0)
            {
                ImGui.TextDisabled("No enabled VIP macros.");
            }
            else
            {
                foreach (var macro in macros)
                {
                    var selected = macro.Id == selectedId;
                    if (ImGui.Selectable($"{macro.Name}##vip-macro-{macro.Id}", selected, ImGuiSelectableFlags.DontClosePopups))
                    {
                        venue.SetActiveVipMacroId(tier.Id, macro.Id);
                        persistence.SaveNow();
                    }
                }
            }

            ImGui.Unindent();
            ImGui.PopID();
            if (tier.Id != tiers[^1].Id)
                ImGui.Separator();
        }

        ImGui.EndCombo();
    }

    private void DrawVenueToolbar()
    {
        var openResetSessionPopup = false;
        var openManualScanPopup = false;

        if (!ImGui.BeginTable(
                "##autogreet-venue-toolbar",
                4,
                ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("venue", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(
            "enabled",
            ImGuiTableColumnFlags.WidthFixed,
            AirTablet.UI.TabletAppTheme.Px(154f));
        ImGui.TableSetupColumn(
            "reset",
            ImGuiTableColumnFlags.WidthFixed,
            AirTablet.UI.TabletAppTheme.Px(118f));
        ImGui.TableSetupColumn(
            "scan",
            ImGuiTableColumnFlags.WidthFixed,
            AirTablet.UI.TabletAppTheme.Px(118f));
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawVenueSelector();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose the venue profile used for detection and greetings.");

        ImGui.TableNextColumn();
        if (venues.IsVenueActive)
        {
            var auto = config.AutoGreetEnabled;
            if (ImGui.Checkbox("Auto-greet", ref auto))
            {
                config.AutoGreetEnabled = auto;
                persistence.SaveNow();
                if (auto)
                    queue.EnqueueEligibleUngreeted(true);
            }
            UiHelpers.TooltipOnHover("Automatically greet eligible guests from the Ungreeted list.");
        }

        ImGui.TableNextColumn();
        ImGui.BeginDisabled(!venues.IsVenueActive);
        if (ImGui.Button(
                "Reset Session##main-reset-session",
                new System.Numerics.Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
        {
            openResetSessionPopup = true;
        }
        ImGui.EndDisabled();

        ImGui.TableNextColumn();
        ImGui.BeginDisabled(!venues.IsVenueActive);
        if (ImGui.Button(
                "Manual Scan##main-manual-scan",
                new System.Numerics.Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
        {
            openManualScanPopup = true;
        }
        UiHelpers.TooltipOnHover("Add currently visible eligible visitors to the greeting list.");
        ImGui.EndDisabled();
        ImGui.EndTable();

        // Open popups after leaving the table so OpenPopup and BeginPopupModal
        // resolve against the same ImGui ID stack.
        if (openResetSessionPopup)
        {
            resetSessionPopupAnchor = UiHelpers.GetPopupPositionNearMouse(
                AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(460f, 190f)));
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Reset session?##main-reset-session-popup");
        }

        if (openManualScanPopup)
        {
            manualScanPopupAnchor = UiHelpers.GetPopupPositionNearMouse(
                AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(500f, 190f)));
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Manual scan?##main-manual-scan-popup");
        }
    }

    private void DrawGreetingWorkspace(VenueProfile venue)
    {
        if (ImGui.BeginChild(
                "##greeting-workspace",
                AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(0, 132f)),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(AutoGreetTheme.PurpleHovered, "Greeting setup");
            ImGui.Separator();
            if (ImGui.BeginTable(
                    "##greeting-selector-grid",
                    2,
                    ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawGreetingProfileSelector(venue);
                ImGui.TableNextColumn();
                DrawMacroSelector(venue, GreetingCategory.FirstTime, "First-time macro");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawMacroSelector(venue, GreetingCategory.Returning, "Returning macro");
                ImGui.TableNextColumn();
                DrawVipMacroSelector(venue);
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
    }

    private void DrawSessionSummary(VenueProfile venue, SessionData session)
    {
        var waiting = venue.Queue.Count(q => q.Status == QueueEntryStatus.Waiting);
        var metrics = new (string Label, string Value)[]
        {
            ("Lifetime", venue.LifetimeVisitors.Count.ToString("N0")),
            ("Visitors", session.NightlyVisitors.Count.ToString("N0")),
            ("Ungreeted", session.Ungreeted.Count.ToString("N0")),
            ("Greeted", session.Greeted.Count.ToString("N0")),
            ("Queue", queue.IsRunning ? $"{waiting:N0} active" : $"{waiting:N0} idle"),
        };

        if (ImGui.BeginTable(
                "##autogreet-session-summary",
                metrics.Length,
                ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            foreach (var metric in metrics)
            {
                ImGui.TableNextColumn();
                ImGui.TextDisabled(metric.Label);
                ImGui.TextColored(AutoGreetTheme.PurpleHovered, metric.Value);
            }
            ImGui.EndTable();
        }
    }

    private void EnsureActiveMacroDefaults(VenueProfile venue)
    {
        foreach (var category in new[] { GreetingCategory.FirstTime, GreetingCategory.Returning })
        {
            var activeId = venue.GetActiveMacroId(category);
            var macros = venues.GetGreetingProfileForVenue(venue).Macros.Where(m => m.Enabled && m.Category == category).ToList();
            if (activeId == Guid.Empty || macros.All(m => m.Id != activeId))
                venue.SetActiveMacroId(category, macros.FirstOrDefault()?.Id ?? Guid.Empty);
        }

        venues.RepairVenueData(venue);
    }

    private void DrawUngreeted(SessionData session)
    {
        UiHelpers.Section($"Ungreeted ({session.Ungreeted.Count})");
        var height = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(150f),
            ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(6f));
        if (ImGui.BeginChild("ungreeted", new System.Numerics.Vector2(0, height), true))
        {
            var i = 0;
            foreach (var key in session.Ungreeted.ToArray())
            {
                DrawVisitorActions(key, greetedList: false, i++);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }

    private void DrawGreeted(SessionData session)
    {
        UiHelpers.Section($"Greeted ({session.Greeted.Count})");
        var height = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(150f),
            ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(6f));
        if (ImGui.BeginChild("greeted", new System.Numerics.Vector2(0, height), true))
        {
            var i = 0;
            foreach (var key in session.Greeted.ToArray())
            {
                DrawVisitorActions(key, greetedList: true, i++);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }

    private void DrawVisitorActions(VisitorKey key, bool greetedList, int index)
    {
        ImGui.PushID($"main-{(greetedList ? "g" : "u")}-{index}-{key}");
        var state = venues.ActiveVenue.Session.NightlyVisitors.FirstOrDefault(v => string.Equals(v.Key.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));
        UiHelpers.VisitorRow(key, state?.Present == true, state?.ReturningThisSession == true, state?.HereWhenArrived == true);
        ImGui.TextDisabled(state is null ? "No session timestamp" : $"Last seen: {state.LastSeenUtc.LocalDateTime:g}");
        if (!greetedList)
        {
            if (ImGui.SmallButton("Greet Now")) queue.Enqueue(key, forceStart: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Skip")) visitors.Skip(key);
            ImGui.SameLine();
            if (ImGui.SmallButton("Mark Greeted")) visitors.MarkGreeted(key);
        }
        else
        {
            if (ImGui.SmallButton("Move to Ungreeted")) visitors.MoveToUngreeted(key);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Blacklist")) visitors.ToggleBlacklist(key);
        ImGui.PopID();
    }
}
