using System.Numerics;
using AirTablet.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Lumina.Excel.Sheets;

namespace PartyRefresh;

internal sealed class Plugin : IDisposable
{
    private const string Version = "1.0.9.0";
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly PartyFinderService partyFinder;
    private readonly FileDialogService dialogs = new();
    private readonly List<(uint Id, string Name)> duties;
    private bool settingsVisible;
    private string statusMessage = string.Empty;
    private string newProfileName = "New Venue";
    private string newPresetName = "New Preset";
    private string dutySearch = string.Empty;
    private bool copyProfile = true;
    private bool copyPreset = true;
    private DeleteTarget pendingDelete;
    private bool confirmEndRecruitment;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Normalize();
        persistence = new PersistenceService(config);
        partyFinder = new PartyFinderService(config, () => Preset);
        settingsVisible = config.SettingsVisible;
        duties = LoadDuties();
    }

    private VenueProfile Profile => persistence.ActiveProfile;
    private PartyFinderPreset Preset => Profile.ActivePreset;

    public void Tick() => partyFinder.Tick();

    public void Draw()
    {
        dialogs.Draw();
        if (settingsVisible)
            DrawSettings();
        else
            DrawMain();
        DrawDeleteConfirmation();
        DrawEndRecruitmentConfirmation();
    }

    public bool CanNavigateBack() => settingsVisible;

    public bool NavigateBack()
    {
        if (!settingsVisible)
            return false;
        settingsVisible = false;
        config.SettingsVisible = false;
        persistence.SaveConfig();
        return true;
    }

    public string? ConsumeNotification() => partyFinder.ConsumeNotification();

    private void DrawHeader(bool showSettings)
    {
        if (!ImGui.BeginTable("##partyrefresh-header", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return;
        ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(210f));
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(90f));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##partyrefresh-profile", Profile.Name))
        {
            foreach (var candidate in persistence.Profiles.OrderBy(profile => profile.Name))
            {
                if (ImGui.Selectable(candidate.Name, candidate.Id == Profile.Id))
                {
                    persistence.ActivateProfile(candidate.Id);
                    dutySearch = string.Empty;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.TableNextColumn();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(partyFinder.IsBusy ? TabletAppTheme.AccentHover : TabletAppTheme.MutedText, partyFinder.Status);
        ImGui.PopTextWrapPos();
        ImGui.TableNextColumn();
        if (showSettings && ImGui.Button("Settings", new Vector2(-1f, 0f)))
        {
            settingsVisible = true;
            config.SettingsVisible = true;
            persistence.SaveConfig();
        }
        ImGui.EndTable();
    }

    private void DrawMain()
    {
        DrawHeader(true);
        ImGui.Separator();
        DrawPrimaryControls();
        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 5f)));

        if (ImGui.BeginTabBar("##partyrefresh-preset-tabs"))
        {
            if (ImGui.BeginTabItem("Details"))
            {
                DrawDetails();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Party & Roles"))
            {
                DrawPartyAndRoles();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Conditions"))
            {
                DrawConditions();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawPrimaryControls()
    {
        if (!ImGui.BeginTable("##partyrefresh-actions", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return;
        ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Post", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(120f));
        ImGui.TableSetupColumn("Refresh", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(120f));
        ImGui.TableSetupColumn("End", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(145f));
        ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed, TabletAppTheme.Px(175f));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##partyrefresh-preset", Preset.Name))
        {
            foreach (var candidate in Profile.Presets.OrderBy(preset => preset.Name))
            {
                if (ImGui.Selectable(candidate.Name, candidate.Id == Preset.Id))
                {
                    persistence.ActivatePreset(candidate.Id);
                    dutySearch = string.Empty;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.TableNextColumn();
        var postDisabled = partyFinder.IsBusy || partyFinder.IsRecruiting;
        if (postDisabled) ImGui.BeginDisabled();
        if (ImGui.Button("Post Preset", new Vector2(-1f, 0f)))
            partyFinder.ApplyPreset(Preset);
        if (postDisabled) ImGui.EndDisabled();
        ImGui.TableNextColumn();
        var refreshDisabled = partyFinder.IsBusy || !partyFinder.IsRecruiting;
        if (refreshDisabled) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh Now", new Vector2(-1f, 0f)))
            partyFinder.RefreshCurrent();
        if (refreshDisabled) ImGui.EndDisabled();
        ImGui.TableNextColumn();
        var endDisabled = partyFinder.IsBusy || !partyFinder.IsRecruiting;
        if (endDisabled) ImGui.BeginDisabled();
        if (ImGui.Button("End Recruitment", new Vector2(-1f, 0f)))
        {
            confirmEndRecruitment = true;
            TabletAppTheme.OpenCenteredModal("End Party Finder recruitment?");
        }
        if (endDisabled) ImGui.EndDisabled();
        ImGui.TableNextColumn();
        var auto = config.AutoRefreshEnabled;
        if (ImGui.Checkbox("Auto refresh", ref auto))
            partyFinder.SetAutoRefresh(auto);
        ImGui.EndTable();

        if (config.AutoRefreshEnabled)
        {
            var remaining = partyFinder.AutomaticRefreshRemaining;
            var countdown = remaining <= TimeSpan.Zero
                ? "00:00"
                : $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
            ImGui.TextColored(TabletAppTheme.AccentHover, $"Next automatic refresh: {countdown}");
            ImGui.SameLine(0f, TabletAppTheme.Px(12f));
            ImGui.TextColored(TabletAppTheme.MutedText, $"Every {config.RefreshIntervalMinutes} minute(s)");
        }
        if (!string.IsNullOrWhiteSpace(statusMessage))
            ImGui.TextWrapped(statusMessage);
    }

    private void DrawDetails()
    {
        if (BeginCard("##partyrefresh-details"))
        {
            SectionTitle($"Preset details · {Preset.Name}");
            var name = Preset.Name;
            ImGui.SetNextItemWidth(MathF.Min(TabletAppTheme.Px(420f), ImGui.GetContentRegionAvail().X));
            if (ImGui.InputText("Preset name", ref name, 64))
            {
                Preset.Name = PartyFinderPreset.CleanName(name, Preset.Name);
                SavePreset();
            }

            var recruitmentType = Preset.RecruitmentType;
            var recruitmentLabels = new[] { "Normal", "Alliance", "Custom Match" };
            ImGui.SetNextItemWidth(TabletAppTheme.Px(250f));
            if (ImGui.Combo("Recruitment type", ref recruitmentType, recruitmentLabels, recruitmentLabels.Length))
            {
                Preset.RecruitmentType = recruitmentType;
                SavePreset();
            }

            var categoryValues = DutyCategories.Select(category => category.Value).ToArray();
            var categoryNames = DutyCategories.Select(category => category.Name).ToArray();
            var categoryIndex = Math.Max(0, Array.IndexOf(categoryValues, Preset.DutyCategoryId));
            ImGui.SetNextItemWidth(TabletAppTheme.Px(320f));
            if (ImGui.Combo("Duty category", ref categoryIndex, categoryNames, categoryNames.Length))
            {
                Preset.DutyCategoryId = categoryValues[categoryIndex];
                Preset.DutyRowId = 0;
                Preset.DutyName = Preset.DutyCategoryId == 0 ? "None" : "All";
                SavePreset();
            }

            if (Preset.DutyCategoryId == 0) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(TabletAppTheme.Px(320f));
            ImGui.InputTextWithHint("##partyrefresh-duty-search", "Search duties...", ref dutySearch, 100);
            var matchingDuties = duties
                .Where(duty => string.IsNullOrWhiteSpace(dutySearch) || duty.Name.Contains(dutySearch.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ImGui.SetNextItemWidth(MathF.Min(TabletAppTheme.Px(520f), ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("Specific duty", Preset.DutyRowId == 0 ? "All duties in category" : Preset.DutyName))
            {
                if (ImGui.Selectable("All duties in category", Preset.DutyRowId == 0))
                {
                    Preset.DutyRowId = 0;
                    Preset.DutyName = "All";
                    SavePreset();
                }
                foreach (var duty in matchingDuties)
                {
                    if (ImGui.Selectable(duty.Name, Preset.DutyRowId == duty.Id))
                    {
                        Preset.DutyRowId = duty.Id;
                        Preset.DutyName = duty.Name;
                        SavePreset();
                    }
                }
                ImGui.EndCombo();
            }
            if (Preset.DutyCategoryId == 0) ImGui.EndDisabled();

            var objective = Preset.ObjectiveId;
            var objectiveLabels = new[] { "None", "Duty Completion", "Practice", "Loot" };
            ImGui.SetNextItemWidth(TabletAppTheme.Px(260f));
            if (ImGui.Combo("Objective", ref objective, objectiveLabels, objectiveLabels.Length))
            {
                Preset.ObjectiveId = objective;
                SavePreset();
            }

            var comment = Preset.Comment;
            ImGui.TextUnformatted("Comment");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextMultiline("##partyrefresh-comment", ref comment, 768, TabletAppTheme.Px(new Vector2(-1f, 92f))))
            {
                Preset.Comment = comment;
                SavePreset();
            }
            ImGui.TextColored(TabletAppTheme.MutedText, $"{System.Text.Encoding.UTF8.GetByteCount(Preset.Comment)} / 191 bytes");
            EndCard();
        }
    }

    private void DrawPartyAndRoles()
    {
        if (BeginCard("##partyrefresh-roles"))
        {
            SectionTitle("Party roles");
            ImGui.TextWrapped("Your current job always occupies the first slot. Configure the seven recruitment slots below.");
            var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / TabletAppTheme.Px(235f)), 1, 4);
            if (ImGui.BeginTable("##partyrefresh-role-grid", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableNextColumn();
                ImGui.BeginDisabled();
                var currentJob = 0;
                ImGui.TextUnformatted("Slot 1");
                ImGui.SetNextItemWidth(-1f);
                ImGui.Combo("##partyrefresh-slot-current", ref currentJob, new[] { "Current job" }, 1);
                ImGui.EndDisabled();
                for (var index = 1; index < 8; index++)
                {
                    ImGui.TableNextColumn();
                    var role = (int)Preset.Slots[index];
                    ImGui.TextUnformatted($"Slot {index + 1}");
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.Combo($"##partyrefresh-slot-{index}", ref role, RoleLabels, RoleLabels.Length))
                    {
                        Preset.Slots[index] = (PartyRefreshRole)role;
                        SavePreset();
                    }
                }
                ImGui.EndTable();
            }
            ImGui.Separator();
            Checkbox("Remove role restrictions for all remaining openings", () => Preset.RemoveRoleRestrictions, value => Preset.RemoveRoleRestrictions = value);
            Checkbox("Unselect Classes", () => Preset.UnselectClasses, value => Preset.UnselectClasses = value);
            Checkbox("One Player per Job", () => Preset.OnePlayerPerJob, value => Preset.OnePlayerPerJob = value);
            EndCard();
        }

        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 7f)));
        if (BeginCard("##partyrefresh-search-area"))
        {
            SectionTitle("Search area");
            Checkbox("Limit Recruiting to World Server", () => Preset.LimitRecruitingToWorld, value => Preset.LimitRecruitingToWorld = value);
            Checkbox("Form a Private Party", () => Preset.FormPrivateParty, value => Preset.FormPrivateParty = value);
            if (!Preset.FormPrivateParty) ImGui.BeginDisabled();
            var password = Preset.PrivatePartyPassword;
            ImGui.SetNextItemWidth(TabletAppTheme.Px(160f));
            if (ImGui.InputInt("Party password", ref password, 1, 10))
            {
                Preset.PrivatePartyPassword = Math.Clamp(password, 0, 9999);
                SavePreset();
            }
            if (!Preset.FormPrivateParty) ImGui.EndDisabled();
            EndCard();
        }
    }

    private void DrawConditions()
    {
        if (BeginCard("##partyrefresh-conditions"))
        {
            SectionTitle("Conditions");
            Checkbox("Completion Status", () => Preset.CompletionStatusEnabled, value => Preset.CompletionStatusEnabled = value);
            if (!Preset.CompletionStatusEnabled) ImGui.BeginDisabled();
            var completion = Preset.CompletionStatusType;
            var completionLabels = new[] { "Duty Complete", "Duty Complete · Weekly Reward Unclaimed", "Duty Incomplete" };
            ImGui.SetNextItemWidth(TabletAppTheme.Px(380f));
            if (ImGui.Combo("Completion requirement", ref completion, completionLabels, completionLabels.Length))
            {
                Preset.CompletionStatusType = completion;
                SavePreset();
            }
            if (!Preset.CompletionStatusEnabled) ImGui.EndDisabled();
            Checkbox("Avg. Item Lv.", () => Preset.AvgItemLevelEnabled, value => Preset.AvgItemLevelEnabled = value);
            if (!Preset.AvgItemLevelEnabled) ImGui.BeginDisabled();
            var itemLevel = Preset.AvgItemLevel;
            ImGui.SetNextItemWidth(TabletAppTheme.Px(180f));
            if (ImGui.InputInt("Average item level", ref itemLevel, 1, 10))
            {
                Preset.AvgItemLevel = Math.Clamp(itemLevel, 1, 999);
                SavePreset();
            }
            if (!Preset.AvgItemLevelEnabled) ImGui.EndDisabled();
            EndCard();
        }

        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 7f)));
        if (BeginCard("##partyrefresh-duty-settings"))
        {
            SectionTitle("Duty Finder settings");
            Checkbox("Unrestricted Party", () => Preset.UnrestrictedParty, value => Preset.UnrestrictedParty = value);
            Checkbox("Minimum IL", () => Preset.MinimumItemLevel, value => Preset.MinimumItemLevel = value);
            Checkbox("Silence Echo", () => Preset.SilenceEcho, value => Preset.SilenceEcho = value);
            var loot = Preset.LootRules;
            ImGui.SetNextItemWidth(TabletAppTheme.Px(230f));
            if (ImGui.Combo("Loot rules", ref loot, new[] { "Normal", "Greed Only", "Lootmaster" }, 3))
            {
                Preset.LootRules = loot;
                SavePreset();
            }
            EndCard();
        }

        ImGui.Dummy(TabletAppTheme.Px(new Vector2(0f, 7f)));
        if (BeginCard("##partyrefresh-languages"))
        {
            SectionTitle("Languages");
            Checkbox("Japanese", () => Preset.Japanese, value => Preset.Japanese = value, sameLine: false);
            Checkbox("English", () => Preset.English, value => Preset.English = value, sameLine: true);
            Checkbox("German", () => Preset.German, value => Preset.German = value, sameLine: true);
            Checkbox("French", () => Preset.French, value => Preset.French = value, sameLine: true);
            EndCard();
        }
    }

    private void DrawSettings()
    {
        DrawHeader(false);
        ImGui.Separator();
        ImGui.TextColored(TabletAppTheme.AccentHover, "PartyRefresh settings");
        ImGui.TextWrapped("Manage automatic refreshing, venue profiles, presets, imports, and exports.");
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##partyrefresh-settings-tabs"))
        {
            if (ImGui.BeginTabItem("Refresh"))
            {
                DrawRefreshSettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Venue Profiles"))
            {
                DrawProfileSettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Presets"))
            {
                DrawPresetSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawRefreshSettings()
    {
        if (BeginCard("##partyrefresh-refresh-settings"))
        {
            SectionTitle("Automatic refresh");
            var enabled = config.AutoRefreshEnabled;
            if (ImGui.Checkbox("Automatically refresh an active recruitment", ref enabled))
                partyFinder.SetAutoRefresh(enabled);
            ImGui.TextWrapped("PartyRefresh opens the current recruitment, selects Edit, applies the currently selected preset, and renews the timer. It waits whenever no active recruitment exists.");
            var interval = config.RefreshIntervalMinutes;
            ImGui.SetNextItemWidth(MathF.Min(TabletAppTheme.Px(420f), ImGui.GetContentRegionAvail().X));
            if (ImGui.SliderInt("Refresh interval", ref interval, 1, 55, "%d min", ImGuiSliderFlags.AlwaysClamp))
            {
                config.RefreshIntervalMinutes = interval;
                persistence.SaveConfig();
                partyFinder.RefreshScheduleChanged();
            }
            EndCard();
        }
    }

    private void DrawProfileSettings()
    {
        if (BeginCard("##partyrefresh-profile-settings"))
        {
            SectionTitle("Venue profiles");
            var profileName = Profile.Name;
            ImGui.SetNextItemWidth(TabletAppTheme.Px(300f));
            if (ImGui.InputText("Current profile name", ref profileName, 64))
            {
                Profile.Name = PartyFinderPreset.CleanName(profileName, Profile.Name);
                persistence.SaveProfile(Profile);
            }
            ImGui.SetNextItemWidth(TabletAppTheme.Px(300f));
            ImGui.InputTextWithHint("##partyrefresh-new-profile", "New venue profile", ref newProfileName, 64);
            ImGui.Checkbox("Copy current venue profile", ref copyProfile);
            if (ImGui.Button("Add Profile", TabletAppTheme.Px(new Vector2(125f, 0f))))
            {
                persistence.AddProfile(newProfileName, copyProfile);
                newProfileName = "New Venue";
            }
            ImGui.SameLine();
            if (persistence.Profiles.Count <= 1) ImGui.BeginDisabled();
            if (ImGui.Button("Delete Profile", TabletAppTheme.Px(new Vector2(135f, 0f))))
            {
                pendingDelete = DeleteTarget.Profile;
                TabletAppTheme.OpenCenteredModal("Delete PartyRefresh profile?");
            }
            if (persistence.Profiles.Count <= 1) ImGui.EndDisabled();
            ImGui.Separator();
            var fileDialogOpen = dialogs.DialogOpen;
            if (fileDialogOpen) ImGui.BeginDisabled();
            if (ImGui.Button(fileDialogOpen ? "File dialog open..." : "Export Profile", TabletAppTheme.Px(new Vector2(150f, 0f))))
            {
                dialogs.Export(Profile.Name + ".json", path =>
                {
                    try { persistence.ExportProfile(Profile, path); statusMessage = "Venue profile exported."; }
                    catch (Exception ex) { statusMessage = ex.Message; }
                });
            }
            ImGui.SameLine();
            if (ImGui.Button(fileDialogOpen ? "File dialog open...##partyrefresh-import" : "Import Profile", TabletAppTheme.Px(new Vector2(150f, 0f))))
            {
                dialogs.Import(path =>
                {
                    try { persistence.ImportProfile(path); statusMessage = "Venue profile imported."; }
                    catch (Exception ex) { statusMessage = ex.Message; }
                });
            }
            if (fileDialogOpen) ImGui.EndDisabled();
            if (!string.IsNullOrWhiteSpace(statusMessage))
                ImGui.TextWrapped(statusMessage);
            EndCard();
        }
    }

    private void DrawPresetSettings()
    {
        if (BeginCard("##partyrefresh-preset-settings"))
        {
            SectionTitle($"Presets in {Profile.Name}");
            ImGui.SetNextItemWidth(TabletAppTheme.Px(300f));
            ImGui.InputTextWithHint("##partyrefresh-new-preset", "New preset name", ref newPresetName, 64);
            ImGui.Checkbox("Copy current preset", ref copyPreset);
            if (ImGui.Button("Add Preset", TabletAppTheme.Px(new Vector2(125f, 0f))))
            {
                persistence.AddPreset(newPresetName, copyPreset);
                newPresetName = "New Preset";
            }
            ImGui.SameLine();
            if (Profile.Presets.Count <= 1) ImGui.BeginDisabled();
            if (ImGui.Button("Delete Preset", TabletAppTheme.Px(new Vector2(135f, 0f))))
            {
                pendingDelete = DeleteTarget.Preset;
                TabletAppTheme.OpenCenteredModal("Delete PartyRefresh preset?");
            }
            if (Profile.Presets.Count <= 1) ImGui.EndDisabled();
            ImGui.Separator();
            foreach (var preset in Profile.Presets.OrderBy(candidate => candidate.Name))
            {
                var selected = preset.Id == Preset.Id;
                if (ImGui.Selectable($"{preset.Name}##partyrefresh-settings-preset-{preset.Id}", selected))
                    persistence.ActivatePreset(preset.Id);
            }
            EndCard();
        }
    }

    private void DrawDeleteConfirmation()
    {
        var modal = pendingDelete == DeleteTarget.Profile
            ? "Delete PartyRefresh profile?"
            : "Delete PartyRefresh preset?";
        if (pendingDelete == DeleteTarget.None ||
            !TabletAppTheme.BeginCenteredModal(modal, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
            return;
        var description = pendingDelete == DeleteTarget.Profile
            ? $"Delete venue profile '{Profile.Name}' and every Party Finder preset inside it?"
            : $"Delete Party Finder preset '{Preset.Name}'?";
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(420f));
        ImGui.TextWrapped(description);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("Delete", TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            if (pendingDelete == DeleteTarget.Profile)
                persistence.DeleteProfile(Profile.Id);
            else
                persistence.DeletePreset(Preset.Id);
            pendingDelete = DeleteTarget.None;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            pendingDelete = DeleteTarget.None;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    private void DrawEndRecruitmentConfirmation()
    {
        const string modal = "End Party Finder recruitment?";
        if (!confirmEndRecruitment ||
            !TabletAppTheme.BeginCenteredModal(modal, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
            return;

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TabletAppTheme.Px(430f));
        ImGui.TextWrapped("End the active Party Finder recruitment? This removes the listing immediately.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (ImGui.Button("End Recruitment", TabletAppTheme.Px(new Vector2(155f, 0f))))
        {
            partyFinder.EndRecruitment();
            confirmEndRecruitment = false;
            TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep Recruiting", TabletAppTheme.Px(new Vector2(155f, 0f))))
        {
            confirmEndRecruitment = false;
            TabletAppTheme.CloseCenteredModal();
        }
        TabletAppTheme.EndCenteredModal();
    }

    internal IReadOnlyList<AirTablet.Services.ControlCenterWidget> GetControlCenterWidgets() =>
    [
        new(
            "partyrefresh.auto",
            "PartyRefresh",
            "Auto refresh",
            "Turn automatic Party Finder refreshing on or off.",
            AirTablet.Services.ControlCenterWidgetKind.Toggle,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            () => new(
                config.AutoRefreshEnabled ? "On" : "Off",
                config.AutoRefreshEnabled ? $"Every {config.RefreshIntervalMinutes} min" : Profile.Name,
                config.AutoRefreshEnabled),
            partyFinder.SetAutoRefresh),
        new(
            "partyrefresh.recruitment",
            "PartyRefresh",
            "Party Finder",
            "Show the active Party Finder recruitment and PartyRefresh status.",
            AirTablet.Services.ControlCenterWidgetKind.Stat,
            AirTablet.Services.ControlCenterWidgetSize.Compact,
            ReadRecruitmentWidget),
    ];

    private AirTablet.Services.ControlCenterWidgetSnapshot ReadRecruitmentWidget()
    {
        if (partyFinder.IsBusy)
            return new("Working", partyFinder.Status, true);
        if (!partyFinder.IsRecruiting)
            return new("Not recruiting", $"{Profile.Name} · {Preset.Name}", false);

        var detail = Preset.Name;
        if (config.AutoRefreshEnabled)
        {
            var remaining = partyFinder.AutomaticRefreshRemaining;
            var countdown = remaining <= TimeSpan.Zero
                ? "00:00"
                : $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
            detail = $"{Preset.Name} · {countdown}";
        }
        return new("Recruiting", detail, true);
    }

    private void Checkbox(string label, Func<bool> read, Action<bool> write, bool sameLine = false)
    {
        if (sameLine)
            ImGui.SameLine(0f, TabletAppTheme.Px(18f));
        var value = read();
        if (!ImGui.Checkbox(label, ref value))
            return;
        write(value);
        SavePreset();
    }

    private void SavePreset() => persistence.SaveProfile(Profile);

    private static bool BeginCard(string id)
    {
        if (!ImGui.BeginTable(id, 1,
                ImGuiTableFlags.BordersOuter |
                ImGuiTableFlags.SizingStretchProp |
                ImGuiTableFlags.NoSavedSettings |
                ImGuiTableFlags.PadOuterX))
            return false;
        ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        return true;
    }

    private static void EndCard() => ImGui.EndTable();
    private static void SectionTitle(string title)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(TabletAppTheme.AccentHover, title);
        ImGui.PopTextWrapPos();
    }

    private static List<(uint Id, string Name)> LoadDuties()
    {
        try
        {
            return DalamudServices.DataManager.GetExcelSheet<ContentFinderCondition>()
                .Where(row => row.RowId > 0 && !string.IsNullOrWhiteSpace(row.Name.ExtractText()))
                .Select(row => (row.RowId, row.Name.ExtractText()))
                .DistinctBy(row => row.RowId)
                .OrderBy(row => row.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "PartyRefresh could not load the duty catalog.");
            return [];
        }
    }

    public void Dispose()
    {
        persistence.SaveConfig();
        persistence.SaveProfile(Profile);
        dialogs.Dispose();
    }

    private static readonly (int Value, string Name)[] DutyCategories =
    [
        (0, "None"), (1, "Duty Roulette"), (2, "Dungeons"), (3, "Guildhests"),
        (4, "Trials"), (5, "Raids"), (6, "High-end Duty"), (7, "PvP"),
        (8, "Gold Saucer"), (9, "FATEs"), (10, "Treasure Hunt"), (11, "The Hunt"),
        (12, "Gathering Forays"), (13, "Deep Dungeons"), (14, "Field Operations"),
        (15, "Variant & Criterion Dungeon"),
    ];

    private static readonly string[] RoleLabels =
    [
        "Free", "Tank", "Healer", "Melee DPS", "Physical Ranged DPS", "Magical Ranged DPS", "Omit Slot",
    ];

    private enum DeleteTarget
    {
        None,
        Profile,
        Preset,
    }
}
