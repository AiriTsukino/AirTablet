using System.Numerics;
using System.Text;
using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class SettingsTab
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly DetectionService detection;
    private readonly GreetingService greetings;
    private readonly SoundService sound;
    private readonly EmoteResumeService emoteResume;
    private readonly GreetingsTab greetingsTab;
    private readonly VenuesTab venuesTab;
    private readonly VipBlacklistTab vipBlacklistTab;
    private readonly object soundPickerGate = new();
    private bool soundPickerOpen;
    private string? pendingSoundPath;
    private string soundPickerStatus = string.Empty;
    private Guid pendingDeleteRegionId = Guid.Empty;
    private Guid dangerZoneVenueId = Guid.Empty;
    private bool openResetGlobalHistoryPopup;
    private bool openResetVenueHistoryPopup;
    private Vector2 deleteRegionPopupAnchor = Vector2.Zero;
    private Vector2 resetGlobalHistoryPopupAnchor = Vector2.Zero;
    private Vector2 resetVenueHistoryPopupAnchor = Vector2.Zero;

    public SettingsTab(
        Configuration config,
        VenueService venues,
        PersistenceService persistence,
        DetectionService detection,
        GreetingService greetings,
        SoundService sound,
        EmoteResumeService emoteResume,
        GreetingsTab greetingsTab,
        VenuesTab venuesTab,
        VipBlacklistTab vipBlacklistTab)
    {
        this.config = config;
        this.venues = venues;
        this.persistence = persistence;
        this.detection = detection;
        this.greetings = greetings;
        this.sound = sound;
        this.emoteResume = emoteResume;
        this.greetingsTab = greetingsTab;
        this.venuesTab = venuesTab;
        this.vipBlacklistTab = vipBlacklistTab;
    }

    public void Draw()
    {
        ApplyPendingSoundPickerResult();

        if (ImGui.BeginTabBar("AutoGreetSettingsSubTabs"))
        {
            if (ImGui.BeginTabItem("General")) { DrawGeneral(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Venues")) { venuesTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Greetings")) { greetingsTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Regions")) { DrawCustomRegionSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("VIP / Blacklist")) { vipBlacklistTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Danger Zone")) { DrawDangerZone(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Help")) { DrawHelpTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Diagnostics")) { DrawDiagnosticsTab(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawGeneral()
    {
        DrawQueueTimingSettings();
        DrawDoorbellSettings();
        DrawChatNotificationSettings();
        DrawGreetingBehaviorSettings();
    }

    private void DrawDiagnosticsTab()
    {
        DrawSoundDiagnostics();
        DrawGreetingBehaviorDiagnostics();
        DrawGreetingDiagnostics();
        DrawDetectionDiagnostics();
    }

    private void DrawQueueTimingSettings()
    {
        UiHelpers.Section("Queue timing");

        var startDelay = config.GreetingStartDelaySeconds;
        if (ImGui.SliderFloat("Delay before greeting starts", ref startDelay, 0, 10, "%.1f sec"))
        {
            config.GreetingStartDelaySeconds = startDelay;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("How long AutoGreet waits after a visitor is queued before it begins running their greeting macro.");

        var queueDelay = config.QueueDelaySeconds;
        if (ImGui.SliderFloat("Spacing between queued visitors", ref queueDelay, 0, 10, "%.1f sec"))
        {
            config.QueueDelaySeconds = queueDelay;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("How long AutoGreet waits between finishing one queued visitor and starting the next one.");
    }

    private void DrawDoorbellSettings()
    {
        UiHelpers.Section("Doorbell alerts");

        var soundEnabled = config.DoorbellSoundEnabled;
        if (ImGui.Checkbox("Doorbell sound alerts", ref soundEnabled))
        {
            config.DoorbellSoundEnabled = soundEnabled;
            persistence.SaveNow();
        }

        var volumePercent = Math.Clamp(config.DoorbellVolume * 100f, 0f, 100f);
        if (ImGui.SliderFloat("Doorbell volume", ref volumePercent, 0f, 100f, "%.0f%%"))
        {
            config.DoorbellVolume = Math.Clamp(volumePercent / 100f, 0f, 1f);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Default is 25%. This controls AutoGreet's local doorbell sound only.");

        ImGui.TextDisabled("Current doorbell sound:");
        ImGui.TextWrapped(sound.EffectiveSoundPath);

        if (soundPickerOpen) ImGui.BeginDisabled();
        if (ImGui.Button(soundPickerOpen ? "Choose Sound... (open)" : "Choose Sound..."))
            OpenNativeSoundPicker();
        if (soundPickerOpen) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear Sound"))
        {
            config.CustomDoorbellSoundPath = string.Empty;
            persistence.SaveNow();
        }

        ImGui.SameLine();
        if (ImGui.Button("Test Sound")) sound.PlayDoorbell();

        UiHelpers.TextDisabledWrapped("Choose an .mp3 or .wav file for a custom doorbell. If no custom file is set, AutoGreet uses the bundled default doorbell sound.");
        // Sound diagnostic status is shown in the Diagnostics tab to keep General focused on settings only.
    }

    private void DrawChatNotificationSettings()
    {
        UiHelpers.Section("Chat notifications");

        var chat = config.ChatNotificationsEnabled;
        if (ImGui.Checkbox("Chat notifications", ref chat))
        {
            config.ChatNotificationsEnabled = chat;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Prints AutoGreet chat messages when visitors are detected entering or already present when you arrive.");

        var blacklistedChat = config.ChatNotificationsForBlacklistedEnabled;
        if (ImGui.Checkbox("Chat notifications for blacklisted visitors", ref blacklistedChat))
        {
            config.ChatNotificationsForBlacklistedEnabled = blacklistedChat;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Blacklisted visitors are still never queued or macro-greeted; this only prints arrival and entry notices.");

        var leaveChat = config.LeaveChatNotificationsEnabled;
        if (ImGui.Checkbox("Chat notifications when visitors leave", ref leaveChat))
        {
            config.LeaveChatNotificationsEnabled = leaveChat;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Prints an AutoGreet chat message when a detected visitor leaves the housing interior. This does not play a doorbell sound.");
    }

    private void DrawGreetingBehaviorSettings()
    {
        UiHelpers.Section("Greeting behavior");

        var resumeEmote = config.ResumePreviousEmoteEnabled;
        if (ImGui.Checkbox("Resume emote after greeting emotes", ref resumeEmote))
        {
            config.ResumePreviousEmoteEnabled = resumeEmote;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("After AutoGreet sends an emote from a greeting macro, AutoGreet runs the slash command entered below.");

        var resumeCommand = config.ResumeEmoteCommand;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(240f));
        if (ImGui.InputText("Resume emote command", ref resumeCommand, 80))
        {
            resumeCommand = resumeCommand.Trim();
            if (!string.IsNullOrWhiteSpace(resumeCommand) && !resumeCommand.StartsWith('/'))
                resumeCommand = "/" + resumeCommand;

            config.ResumeEmoteCommand = resumeCommand;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Example: /beesknees, /hum, /mandervilledance, /sit.");

        if (!config.ResumePreviousEmoteEnabled) ImGui.BeginDisabled();
        var resumeDelay = Math.Clamp(config.ResumeEmoteDelaySeconds, 0.5f, 15.0f);
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220f));
        if (ImGui.SliderFloat("Resume emote delay", ref resumeDelay, 0.5f, 15.0f, "%.1f sec"))
        {
            config.ResumeEmoteDelaySeconds = Math.Clamp(resumeDelay, 0.5f, 15.0f);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("How long AutoGreet waits after the last greeting emote is sent before running the configured resume emote command.");
        if (!config.ResumePreviousEmoteEnabled) ImGui.EndDisabled();

        var untarget = config.UntargetAfterGreeting;
        if (ImGui.Checkbox("Untarget after greeting", ref untarget))
        {
            config.UntargetAfterGreeting = untarget;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Clears your current target after AutoGreet finishes a greeting target action, such as a targeted emote.");

        var waitForTarget = config.WaitForVisibleTargetBeforeEmote;
        if (ImGui.Checkbox("Queue targeted emotes until targetable", ref waitForTarget))
        {
            config.WaitForVisibleTargetBeforeEmote = waitForTarget;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("When enabled, targeted emotes such as /dote <t> are held in an emote queue until AutoGreet can target the correct visitor. The queue checks pending emotes every 3 seconds.");

        var greetingTimer = config.GreetingTimerEnabled;
        if (ImGui.Checkbox("Greeting timer for returning visitors", ref greetingTimer))
        {
            config.GreetingTimerEnabled = greetingTimer;
            config.GreetingTimerMinutes = Math.Clamp(config.GreetingTimerMinutes, 1, 360);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Allows visitors who already received a main greeting this session to be queued again when they re-enter after the selected number of minutes has passed since that greeting.");

        if (!config.GreetingTimerEnabled) ImGui.BeginDisabled();
        var timerMinutes = Math.Clamp(config.GreetingTimerMinutes, 1, 360);
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220f));
        if (ImGui.SliderInt("Re-greet after", ref timerMinutes, 1, 360, "%d min"))
        {
            config.GreetingTimerMinutes = Math.Clamp(timerMinutes, 1, 360);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("1 minute to 6 hours. Returning visitors are queued for the returning macro when this much time has passed since their last main greeting and they re-enter.");

        var timerManual = config.GreetingTimerMinutes;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(140f));
        if (ImGui.InputInt("Re-greet minutes", ref timerManual, 1, 15))
        {
            config.GreetingTimerMinutes = Math.Clamp(timerManual, 1, 360);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Type the timer in minutes. Example: 120 means 2 hours.");
        if (!config.GreetingTimerEnabled) ImGui.EndDisabled();

        // Resume emote diagnostic status is shown in the Diagnostics tab to keep General focused on settings only.
    }

    private void DrawSoundDiagnostics()
    {
        UiHelpers.Section("Sound diagnostics");
        ImGui.TextWrapped($"Sound status: {sound.LastSoundStatus}");
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(soundPickerStatus)
            ? "Sound picker: idle"
            : $"Sound picker: {soundPickerStatus}");
    }

    private void DrawGreetingBehaviorDiagnostics()
    {
        UiHelpers.Section("Greeting behavior diagnostics");
        ImGui.TextWrapped($"Resume emote status: {emoteResume.LastStatus}");
    }

    private void DrawGreetingDiagnostics()
    {
        UiHelpers.Section("Greeting diagnostics");
        ImGui.TextWrapped($"Last command sent: {greetings.LastCommandText}");
        ImGui.TextWrapped($"Last outgoing tell observed: {greetings.LastOutgoingTellObserved}");
        ImGui.TextWrapped($"Last greeting confirmation: {greetings.LastGreetingConfirmation}");
    }

    private static byte[] CreateReadOnlyBuffer(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[bytes.Length + 1];
        bytes.CopyTo(buffer, 0);
        return buffer;
    }

    private void DrawDetectionDiagnostics()
    {
        UiHelpers.Section("Detection diagnostics");
        ImGui.Text($"Current location: {HousingLocationFormatter.GetTerritoryDisplayName(detection.CurrentTerritoryType)}");
        ImGui.Text($"Scan active: {(detection.IsScanningActive ? "yes" : "no")}");
        ImGui.Text($"Housing scan active: {(detection.IsInHousingInterior ? "yes" : "no")}");
        ImGui.Text($"Custom region active: {(detection.IsInCustomRegionTerritory ? "yes" : "no")}");
        ImGui.Text($"Detected player actors: {detection.CurrentPlayerObjectCount}");
        ImGui.Text($"Cached visitors present: {detection.PresentKeys.Count}");
        ImGui.TextWrapped($"Current plot lock location: {detection.CurrentPlotLockStatus}");
        ImGui.TextWrapped(detection.LastStatus);

        UiHelpers.TextDisabledWrapped("AutoGreet automatically scans housing interiors when the game's housing manager reports that you are inside a house/apartment. Outdoor or non-housing detection is configured from the Regions tab.");
    }


    private void DrawHelpTab()
    {
        UiHelpers.Section("Macro syntax help");
        UiHelpers.TextDisabledWrapped("Supported macro syntax and emote commands for greeting macros. Copy this text when sharing a macro or asking for syntax help.");

        if (ImGui.Button("Copy all help text"))
            ImGui.SetClipboardText(DiagnosticLogService.FullSupportedSyntaxText);

        ImGui.SameLine();
        if (ImGui.Button("Copy emote commands"))
            ImGui.SetClipboardText(EmoteCommandRegistry.SupportedCommandsText);

        ImGui.Spacing();

        var syntaxText = DiagnosticLogService.SupportedSyntaxText;
        var syntaxBuffer = CreateReadOnlyBuffer(syntaxText);
        ImGui.TextUnformatted("Supported syntax");
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(-1f));
        ImGui.InputTextMultiline("##autogreet-help-supported-syntax", syntaxBuffer.AsSpan(), AirTablet.UI.TabletAppTheme.Px(new Vector2(-1f, 150f)), ImGuiInputTextFlags.ReadOnly);

        ImGui.Spacing();

        var emoteText = EmoteCommandRegistry.SupportedCommandsText;
        var emoteBuffer = CreateReadOnlyBuffer(emoteText);
        ImGui.TextUnformatted($"Supported emote commands ({EmoteCommandRegistry.SupportedCommands.Count})");
        var height = Math.Max(
            AirTablet.UI.TabletAppTheme.Px(220f),
            ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(12f));
        ImGui.InputTextMultiline("##autogreet-help-emote-commands", emoteBuffer.AsSpan(), new Vector2(-1f, height), ImGuiInputTextFlags.ReadOnly);
    }

    private void ClearActiveSessionDetectionLists()
    {
        var venue = venues.ActiveVenue;
        venue.Session.Ungreeted.Clear();
        venue.Queue.Clear();
    }

    private void DrawCustomRegionSettings()
    {
        UiHelpers.Section("Regions");
        UiHelpers.TextDisabledWrapped("Regions let AutoGreet detect visitors in non-housing zones or specific parts of a housing/location map. They are saved in AutoGreet\\CustomRegions.json and can be viewed and edited here even when you are not currently in their zone.");
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.25f, 0.25f, 1f));
        ImGui.TextWrapped("Warning: Be careful creating regions in public city states or other crowded zones. AutoGreet will detect anyone inside the region, and large crowds may reduce performance or create noisy visitor lists.");
        ImGui.PopStyleColor();

        if (ImGui.Button("Create Region at My Feet"))
            detection.CreateRegionAtLocalPlayer();
        UiHelpers.TooltipOnHover("Creates a new custom detection region centered at your current character position. Default size is a 5-yalm radius sphere. Cube regions default to 5 x 5 x 5 yalms.");

        var regions = persistence.CustomRegions
            .OrderBy(r => HousingLocationFormatter.GetTerritoryDisplayName(r.TerritoryType), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (regions.Length == 0)
        {
            ImGui.TextDisabled("No custom regions have been created yet.");
            return;
        }

        foreach (var region in regions)
        {
            ImGui.PushID($"region-{region.Id}");
            var regionHeader = HousingLocationFormatter.GetRegionDisplayName(region);
            if (ImGui.TreeNodeEx(regionHeader, ImGuiTreeNodeFlags.None))
            {
                var isCurrentTerritory = region.TerritoryType == detection.CurrentTerritoryType;
                ImGui.TextDisabled($"Location: {HousingLocationFormatter.GetTerritoryDisplayName(region.TerritoryType)}");
                if (!isCurrentTerritory)
                    UiHelpers.TextDisabledWrapped("You can edit this saved region here. Its overlay and Move Center button only work when you are currently in that location.");

                var enabled = region.Enabled;
                if (ImGui.Checkbox("Enabled", ref enabled))
                {
                    region.Enabled = enabled;
                    if (!enabled) ClearActiveSessionDetectionLists();
                    detection.ClearPresenceCache();
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("When enabled, AutoGreet scans this fixed world-space region for visitor entry and leave events.");

                ImGui.SameLine();
                var show = region.ShowOverlay;
                if (ImGui.Checkbox("Show region", ref show))
                {
                    region.ShowOverlay = show;
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("Draws the region outline on screen so you can position and size it. Turn it off when setup is done.");

                var color = HexToVector3(region.DisplayColorHex);
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(180));
                if (ImGui.ColorEdit3("Region color", ref color))
                {
                    region.DisplayColorHex = Vector3ToHex(color);
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("Color used for the custom region wireframe overlay. The default is fully red so it is easy to see while setting up an outdoor event region.");

                var hex = NormalizeHexColor(region.DisplayColorHex);
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(120));
                if (ImGui.InputText("Hex color", ref hex, 16, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    region.DisplayColorHex = NormalizeHexColor(hex);
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("Enter a hex color like #FF0000. Press Enter after editing to apply it.");

                var name = region.Name;
                if (ImGui.InputText("Name", ref name, 80, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    region.Name = string.IsNullOrWhiteSpace(name) ? "Outdoor region" : name.Trim();
                    persistence.SaveNow();
                }

                var shape = (int)region.Shape;
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(180));
                if (ImGui.Combo("Shape", ref shape, "Sphere (3D)\0Cube (3D)\0"))
                {
                    region.Shape = (CustomDetectionRegionShape)Math.Clamp(shape, 0, 1);
                    detection.ClearPresenceCache();
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("The overlay is drawn as a 3D wireframe so height/depth are visible while Show region is enabled.");

                var yawDegrees = region.YawDegrees;
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220));
                if (ImGui.DragFloat("Y rotation (degrees)", ref yawDegrees, 0.5f, -180f, 180f, "%.1f°"))
                {
                    region.YawDegrees = NormalizeDegrees(yawDegrees);
                    detection.ClearPresenceCache();
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("Rotates this region around the vertical Y axis. This is most useful for cube regions when the event area is angled relative to the world grid.");

                if (region.Shape == CustomDetectionRegionShape.Sphere)
                {
                    var radius = region.Radius;
                    ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220));
                    if (ImGui.DragFloat("Radius (yalms)", ref radius, 0.1f, 1f, 100f, "%.1f"))
                    {
                        region.Radius = Math.Clamp(radius, 1f, 100f);
                        detection.ClearPresenceCache();
                        persistence.SaveNow();
                    }
                    UiHelpers.TooltipOnHover("Drag left/right to adjust, or Ctrl+click the field to type an exact value.");
                }
                else
                {
                    var size = region.HalfExtents * 2f;
                    ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(320));
                    if (ImGui.DragFloat3("Cube size XYZ (yalms)", ref size, 0.1f, 1f, 200f, "%.1f"))
                    {
                        region.HalfExtents = new Vector3(
                            Math.Clamp(size.X / 2f, 0.5f, 100f),
                            Math.Clamp(size.Y / 2f, 0.5f, 100f),
                            Math.Clamp(size.Z / 2f, 0.5f, 100f));
                        detection.ClearPresenceCache();
                        persistence.SaveNow();
                    }
                    UiHelpers.TooltipOnHover("Drag values left/right to adjust, or Ctrl+click a field to type. X and Z are horizontal size; Y is vertical height.");
                }

                var center = region.Center;
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(320));
                if (ImGui.DragFloat3("Center XYZ", ref center, 0.1f, float.MinValue, float.MaxValue, "%.2f"))
                {
                    region.Center = center;
                    detection.ClearPresenceCache();
                    persistence.SaveNow();
                }
                UiHelpers.TooltipOnHover("The fixed world-space center of the detection region. Drag values left/right to offset it, or Ctrl+click a field to type.");

                if (!isCurrentTerritory) ImGui.BeginDisabled();
                if (ImGui.Button("Move Center to My Feet"))
                {
                    var local = DalamudServices.ObjectTable.LocalPlayer;
                    if (local is not null)
                    {
                        region.Center = local.Position;
                        detection.ClearPresenceCache();
                        persistence.SaveNow();
                    }
                }
                if (!isCurrentTerritory) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Delete Region"))
                {
                    pendingDeleteRegionId = region.Id;
                    deleteRegionPopupAnchor = UiHelpers.GetPopupPositionNearMouse(AirTablet.UI.TabletAppTheme.Px(new Vector2(380f, 170f)));
                    AirTablet.UI.TabletAppTheme.OpenCenteredModal($"Delete Region?##delete-region-{region.Id}");
                }

                UiHelpers.SetNextPopupPositionNearMouse(deleteRegionPopupAnchor, AirTablet.UI.TabletAppTheme.Px(new Vector2(380f, 170f)));
                if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                        $"Delete Region?##delete-region-{region.Id}",
                        ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextWrapped($"Delete custom region '{region.Name}'? This cannot be undone.");
                    ImGui.Separator();
                    if (ImGui.Button("Delete", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
                    {
                        if (pendingDeleteRegionId == region.Id)
                        {
                            ClearActiveSessionDetectionLists();
                            detection.DeleteRegion(region.Id);
                            pendingDeleteRegionId = Guid.Empty;
                        }
                        deleteRegionPopupAnchor = Vector2.Zero;
                        AirTablet.UI.TabletAppTheme.CloseCenteredModal();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
                    {
                        pendingDeleteRegionId = Guid.Empty;
                        deleteRegionPopupAnchor = Vector2.Zero;
                        AirTablet.UI.TabletAppTheme.CloseCenteredModal();
                    }
                    AirTablet.UI.TabletAppTheme.EndCenteredModal();
                }

                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    private void DrawDangerZone()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.22f, 0.035f, 0.045f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.95f, 0.18f, 0.20f, 0.85f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, AirTablet.UI.TabletAppTheme.Px(8f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, AirTablet.UI.TabletAppTheme.Px(new Vector2(12f, 10f)));
        if (ImGui.BeginChild("DangerZonePanel", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 250)), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            ImGui.TextColored(new Vector4(1f, 0.32f, 0.32f, 1f), "Danger zone");
            ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.95f, 0.18f, 0.20f, 0.95f));
            ImGui.Separator();
            ImGui.PopStyleColor();
            ImGui.TextWrapped("These actions permanently delete visitor history data. They do not delete venues, greeting profiles, macros, VIP settings, blacklist entries, or current general settings.");
            ImGui.Spacing();

            if (DangerButton("Reset Global Visitor History"))
            {
                resetGlobalHistoryPopupAnchor = UiHelpers.GetPopupPositionNearMouse(AirTablet.UI.TabletAppTheme.Px(new Vector2(430f, 180f)));
                openResetGlobalHistoryPopup = true;
            }
            UiHelpers.TooltipOnHover("Deletes lifetime visitor history for every venue. This resets returning-visitor history globally, but keeps venue profiles, greeting profiles, VIPs, blacklists, sessions, and settings.");

            ImGui.Spacing();
            var venueList = venues.Venues.ToArray();
            if (venueList.Length == 0)
            {
                ImGui.TextDisabled("No venues available.");
            }
            else
            {
                if (dangerZoneVenueId == Guid.Empty || venueList.All(v => v.Id != dangerZoneVenueId))
                    dangerZoneVenueId = venues.ActiveVenue.Id;

                var selectedIndex = Math.Max(0, Array.FindIndex(venueList, v => v.Id == dangerZoneVenueId));
                var selectedName = venueList[selectedIndex].Name;
                ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(260));
                if (ImGui.BeginCombo("Venue history to reset", selectedName))
                {
                    for (var i = 0; i < venueList.Length; i++)
                    {
                        var venue = venueList[i];
                        var selected = i == selectedIndex;
                        if (ImGui.Selectable($"{venue.Name}##danger-venue-{venue.Id}", selected))
                            dangerZoneVenueId = venue.Id;
                        if (selected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                if (DangerButton("Reset Selected Venue History"))
                {
                    resetVenueHistoryPopupAnchor = UiHelpers.GetPopupPositionNearMouse(AirTablet.UI.TabletAppTheme.Px(new Vector2(430f, 190f)));
                    openResetVenueHistoryPopup = true;
                }
                UiHelpers.TooltipOnHover("Deletes lifetime visitor history only for the selected venue. This resets returning-visitor history for that venue, but keeps its macros, VIPs, blacklist, session lists, queue, and settings.");
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        if (openResetGlobalHistoryPopup)
        {
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Reset Global Visitor History?##danger-reset-global-history");
            openResetGlobalHistoryPopup = false;
        }

        if (openResetVenueHistoryPopup)
        {
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Reset Selected Venue History?##danger-reset-venue-history");
            openResetVenueHistoryPopup = false;
        }

        UiHelpers.SetNextPopupPositionNearMouse(resetGlobalHistoryPopupAnchor, AirTablet.UI.TabletAppTheme.Px(new Vector2(430f, 180f)));
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Reset Global Visitor History?##danger-reset-global-history",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(1f, 0.32f, 0.32f, 1f), "Permanent action");
            ImGui.TextWrapped("Delete lifetime visitor history for every venue? This cannot be undone.");
            ImGui.Separator();
            if (DangerButton("Reset All History", AirTablet.UI.TabletAppTheme.Px(new Vector2(150, 0))))
            {
                foreach (var venue in venues.Venues)
                    venue.LifetimeVisitors.Clear();
                persistence.SaveNow();
                resetGlobalHistoryPopupAnchor = Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
            {
                resetGlobalHistoryPopupAnchor = Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }

        UiHelpers.SetNextPopupPositionNearMouse(resetVenueHistoryPopupAnchor, AirTablet.UI.TabletAppTheme.Px(new Vector2(430f, 190f)));
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Reset Selected Venue History?##danger-reset-venue-history",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            var venue = venues.Venues.FirstOrDefault(v => v.Id == dangerZoneVenueId);
            ImGui.TextColored(new Vector4(1f, 0.32f, 0.32f, 1f), "Permanent action");
            ImGui.TextWrapped(venue is null
                ? "No venue selected."
                : $"Delete lifetime visitor history for '{venue.Name}'? This cannot be undone.");
            ImGui.Separator();
            if (venue is null) ImGui.BeginDisabled();
            if (DangerButton("Reset Venue History", AirTablet.UI.TabletAppTheme.Px(new Vector2(160, 0))))
            {
                venue?.LifetimeVisitors.Clear();
                persistence.SaveNow();
                resetVenueHistoryPopupAnchor = Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            if (venue is null) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
            {
                resetVenueHistoryPopupAnchor = Vector2.Zero;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }
    }

    private static bool DangerButton(string label)
        => DangerButton(label, Vector2.Zero);

    private static bool DangerButton(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.04f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.08f, 0.10f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.95f, 0.12f, 0.14f, 1f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private static float NormalizeDegrees(float degrees)
    {
        while (degrees > 180f) degrees -= 360f;
        while (degrees < -180f) degrees += 360f;
        return degrees;
    }

    private static Vector3 HexToVector3(string? hex)
    {
        var normalized = NormalizeHexColor(hex);
        var r = Convert.ToByte(normalized.Substring(1, 2), 16) / 255f;
        var g = Convert.ToByte(normalized.Substring(3, 2), 16) / 255f;
        var b = Convert.ToByte(normalized.Substring(5, 2), 16) / 255f;
        return new Vector3(r, g, b);
    }

    private static string Vector3ToHex(Vector3 color)
    {
        var r = (int)MathF.Round(Math.Clamp(color.X, 0f, 1f) * 255f);
        var g = (int)MathF.Round(Math.Clamp(color.Y, 0f, 1f) * 255f);
        var b = (int)MathF.Round(Math.Clamp(color.Z, 0f, 1f) * 255f);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static string NormalizeHexColor(string? hex)
    {
        var value = string.IsNullOrWhiteSpace(hex) ? "#FF0000" : hex.Trim();
        if (!value.StartsWith("#", StringComparison.Ordinal)) value = "#" + value;
        if (value.Length == 9) value = value[..7];
        if (value.Length != 7) return "#FF0000";

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i])) return "#FF0000";
        }

        return value.ToUpperInvariant();
    }

    private void ApplyPendingSoundPickerResult()
    {
        string? selected = null;
        lock (soundPickerGate)
        {
            if (pendingSoundPath is not null)
            {
                selected = pendingSoundPath;
                pendingSoundPath = null;
            }
        }

        if (string.IsNullOrWhiteSpace(selected)) return;
        config.CustomDoorbellSoundPath = selected;
        soundPickerStatus = $"Selected {Path.GetFileName(selected)}.";
        persistence.SaveNow();
    }

    private void OpenNativeSoundPicker()
    {
        if (soundPickerOpen) return;
        soundPickerOpen = true;
        soundPickerStatus = "Opening file picker...";

        var initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(config.CustomDoorbellSoundPath) && File.Exists(config.CustomDoorbellSoundPath))
            initialDirectory = Path.GetDirectoryName(config.CustomDoorbellSoundPath) ?? initialDirectory;
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Choose AutoGreet doorbell sound",
                    Filter = "Sound files (*.mp3;*.wav)|*.mp3;*.wav|MP3 files (*.mp3)|*.mp3|WAV files (*.wav)|*.wav|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                    InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                };

                var result = dialog.ShowDialog();
                lock (soundPickerGate)
                {
                    if (result == System.Windows.Forms.DialogResult.OK && IsSupportedSoundFile(dialog.FileName))
                    {
                        pendingSoundPath = dialog.FileName;
                        soundPickerStatus = "Sound selected.";
                    }
                    else if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        soundPickerStatus = "Please choose an .mp3 or .wav file.";
                    }
                    else
                    {
                        soundPickerStatus = "Sound picker cancelled.";
                    }
                }
            }
            catch (Exception ex)
            {
                lock (soundPickerGate)
                    soundPickerStatus = ex.Message;
            }
            finally
            {
                soundPickerOpen = false;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static bool IsSupportedSoundFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
    }
}
