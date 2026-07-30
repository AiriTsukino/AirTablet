using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ShiftKeeper.Models;
using ShiftKeeper.Services;
using ShiftKeeper.UI.Components;

namespace ShiftKeeper.UI;

public sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly TradePaymentService tradePayments;
    private readonly TargetingService targeting;
    private readonly FileDialogService dialogs;
    private readonly Action<StaffMember> openTell;
    private readonly Action openSettings;
    private int selectedStaffIndex = -1;
    private string newVenueName = string.Empty;
    private string newShiftName = string.Empty;
    private string profileStatus = string.Empty;
    private string staffStatus = string.Empty;
    private Guid pendingDeleteStaffId;
    private bool openDeleteStaffPopup;
    private bool openResetNightPopup;

    public MainWindow(Configuration config, PersistenceService persistence, TradePaymentService tradePayments, TargetingService targeting, FileDialogService dialogs, Action<StaffMember> openTell, Action openSettings)
        : base("ShiftKeeper Dashboard###ShiftKeeperMainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.config = config;
        this.persistence = persistence;
        this.tradePayments = tradePayments;
        this.targeting = targeting;
        this.dialogs = dialogs;
        this.openTell = openTell;
        this.openSettings = openSettings;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(880, 590)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw() => ShiftKeeperTheme.Push();
    public override void PostDraw() => ShiftKeeperTheme.Pop();

    public override void Draw()
    {
        var venue = persistence.ActiveVenue;
        DrawDashboardHeader(venue);
        DrawSummaryCards(venue);
        DrawDashboard(venue);
        if (openResetNightPopup)
        {
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Reset Venue Night?##shift-keeper-reset-night");
            openResetNightPopup = false;
        }
        DrawResetConfirmation(venue);
    }

    public void DrawSettings()
    {
        var venue = persistence.ActiveVenue;
        DrawSettingsHeader(venue);
        ImGui.Separator();
        if (ImGui.BeginTabBar("shift-keeper-settings-tabs"))
        {
            if (ImGui.BeginTabItem("Staff List")) { DrawStaffList(venue); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Shifts & Pay")) { DrawShiftsAndPay(venue); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Venue Profiles")) { DrawProfiles(venue); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        if (openDeleteStaffPopup)
        {
            AirTablet.UI.TabletAppTheme.OpenCenteredModal("Remove Staff?##shift-keeper-remove-staff");
            openDeleteStaffPopup = false;
        }
        DrawDeleteStaffConfirmation(venue);
    }

    private void DrawDashboardHeader(VenueProfile venue)
    {
        ImGui.BeginChild(
            "dashboard-hero",
            new Vector2(-1, AirTablet.UI.TabletAppTheme.Px(104f)),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.TextColored(ShiftKeeperTheme.Purple, venue.Name);
        ImGui.TextDisabled("STAFF OPERATIONS DASHBOARD");
        ImGui.SetNextItemWidth(MathF.Min(AirTablet.UI.TabletAppTheme.Px(340f), width * 0.42f));
        DrawVenueSelector(venue, "dashboard");

        var settingsWidth = AirTablet.UI.TabletAppTheme.Px(118f);
        var resetWidth = AirTablet.UI.TabletAppTheme.Px(126f);
        var gap = AirTablet.UI.TabletAppTheme.Px(10f);
        var totalButtonWidth = settingsWidth + resetWidth + gap;
        var buttonStart = MathF.Max(AirTablet.UI.TabletAppTheme.Px(360f), width - totalButtonWidth);
        ImGui.SetCursorPos(new Vector2(buttonStart, AirTablet.UI.TabletAppTheme.Px(10f)));
        if (ImGui.Button("Settings", new Vector2(settingsWidth, AirTablet.UI.TabletAppTheme.Px(30f)))) openSettings();
        UiHelpers.Help("Open the separate ShiftKeeper settings window for staff, shifts, pay, and venue profiles.");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("Reset Night", new Vector2(resetWidth, AirTablet.UI.TabletAppTheme.Px(30f)))) openResetNightPopup = true;
        UiHelpers.Help("Archive this night, clear timers and paid states, and begin a fresh venue night.");
        var resetButtonX = buttonStart + settingsWidth + gap;
        ImGui.SetCursorPos(new Vector2(resetButtonX, AirTablet.UI.TabletAppTheme.Px(48f)));
        ImGui.TextDisabled("Night started");
        ImGui.SetCursorPosX(resetButtonX);
        ImGui.TextWrapped(venue.CurrentNight.StartedUtc.LocalDateTime.ToString("g"));
        ImGui.EndChild();
    }

    private void DrawSettingsHeader(VenueProfile venue)
    {
        ImGui.SetNextItemWidth(MathF.Min(AirTablet.UI.TabletAppTheme.Px(380f), ImGui.GetContentRegionAvail().X * 0.55f));
        DrawVenueSelector(venue, "settings");
        UiHelpers.Help("Settings and permanent staff are isolated to the selected venue profile.");
    }

    private void DrawVenueSelector(VenueProfile venue, string id)
    {
        if (ImGui.BeginCombo($"Venue##active-venue-{id}", venue.Name))
        {
            foreach (var option in persistence.Venues)
            {
                if (ImGui.Selectable($"{option.Name}##venue-{id}-{option.Id}", option.Id == venue.Id))
                {
                    config.ActiveVenueId = option.Id;
                    selectedStaffIndex = -1;
                    persistence.SaveNow();
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawSummaryCards(VenueProfile venue)
    {
        var scheduled = venue.Staff.Count(s => s.Enabled && venue.CurrentNight.GetOrCreate(s.Id).Scheduled);
        var visible = venue.Staff.Count(s => s.Enabled && venue.CurrentNight.GetOrCreate(s.Id).IsVisible);
        var unpaidStaff = venue.Staff.Where(s => s.Enabled && !venue.CurrentNight.GetOrCreate(s.Id).Paid && CalculateDue(s, venue) > 0).ToList();
        var payrollDue = unpaidStaff.Sum(s => CalculateDue(s, venue));
        var cardGaps = ImGui.GetStyle().ItemSpacing.X * 3f;
        var cardWidth = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(140f),
            (ImGui.GetContentRegionAvail().X - cardGaps) / 4f);
        DrawSummaryCard("SCHEDULED", scheduled.ToString("N0"), ShiftKeeperTheme.Purple, cardWidth);
        ImGui.SameLine();
        DrawSummaryCard("IN VENUE", visible.ToString("N0"), ShiftKeeperTheme.Green, cardWidth);
        ImGui.SameLine();
        DrawSummaryCard("UNPAID STAFF", unpaidStaff.Count.ToString("N0"), ShiftKeeperTheme.Amber, cardWidth);
        ImGui.SameLine();
        DrawSummaryCard("PAYROLL DUE", $"{payrollDue:N0} {venue.CurrencyLabel}", new Vector4(0.86f, 0.62f, 1f, 1f), cardWidth);
    }

    private static void DrawSummaryCard(string label, string value, Vector4 color, float width)
    {
        ImGui.BeginChild(
            $"summary-{label}",
            new Vector2(width, AirTablet.UI.TabletAppTheme.Px(72f)),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextDisabled(label);
        ImGui.TextColored(color, value);
        ImGui.EndChild();
    }

    private void DrawDashboard(VenueProfile venue)
    {
        ImGui.BeginChild("dashboard-scroll", Vector2.Zero, false);
        ImGui.TextDisabled("Live staff roster");
        ImGui.SameLine();
        ImGui.TextDisabled("• Green: present   • Gold: crash grace   • Gray: away/off");
        ImGui.Spacing();

        var activeStaff = venue.Staff.Where(x => x.Enabled).OrderBy(x => x.Role).ThenBy(x => x.Name).ToList();
        if (activeStaff.Count == 0)
        {
            ImGui.TextWrapped("No staff have been added to this venue yet. Open Settings to add someone manually or from your current target.");
            ImGui.EndChild();
            return;
        }

        var tonightWidth = ContentColumnWidth("Tonight", []);
        var roleWidth = ContentColumnWidth(
            "Role",
            activeStaff.Select(x => x.DisplayRole));
        var statusWidth = ContentColumnWidth(
            "Status",
            activeStaff.SelectMany(member =>
            {
                var record = venue.CurrentNight.GetOrCreate(member.Id);
                var shiftSummary = venue.GetShiftSummary(member);
                return string.IsNullOrWhiteSpace(shiftSummary)
                    ? [PresenceStatusText(member, record)]
                    : new[]
                    {
                        PresenceStatusText(member, record),
                        shiftSummary,
                    };
            }));
        var workedWidth = ContentColumnWidth(
            "Worked",
            activeStaff.Select(x =>
                StaffTrackingService.FormatDuration(
                    venue.CurrentNight.GetOrCreate(x.Id).AccruedSeconds)));
        var payWidth = ContentColumnWidth(
            "Pay Due",
            activeStaff.Select(x =>
                $"{CalculateDue(x, venue):N0} {venue.CurrencyLabel}"));
        var paidWidth = ContentColumnWidth("Paid", []);
        var actionsWidth = MathF.Max(
            ContentColumnWidth("Actions", []),
            activeStaff.Max(x =>
                ActionRowWidth(
                    x,
                    venue.CurrentNight.GetOrCreate(x.Id))));

        var flags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingStretchProp
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.NoSavedSettings;
        if (ImGui.BeginTable("staff-dashboard-table", 8, flags, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, -1))))
        {
            ImGui.TableSetupColumn("Tonight", ImGuiTableColumnFlags.WidthFixed, tonightWidth);
            ImGui.TableSetupColumn("Staff", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, roleWidth);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, statusWidth);
            ImGui.TableSetupColumn("Worked", ImGuiTableColumnFlags.WidthFixed, workedWidth);
            ImGui.TableSetupColumn("Pay Due", ImGuiTableColumnFlags.WidthFixed, payWidth);
            ImGui.TableSetupColumn("Paid", ImGuiTableColumnFlags.WidthFixed, paidWidth);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionsWidth);
            ImGui.TableHeadersRow();

            foreach (var member in activeStaff)
            {
                var record = venue.CurrentNight.GetOrCreate(member.Id);
                ImGui.PushID(member.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var scheduledForNight = record.Scheduled;
                if (DrawRosterCheckbox("##scheduled", ref scheduledForNight)) { record.Scheduled = scheduledForNight; persistence.SaveNow(); }
                UiHelpers.Help("Include this staff member in the active venue night. Unscheduling pauses their timer without removing them from the permanent staff list.");

                ImGui.TableNextColumn(); ImGui.TextWrapped(member.Name); ImGui.TextDisabled(member.World);
                ImGui.TableNextColumn(); ImGui.TextWrapped(member.DisplayRole);
                ImGui.TableNextColumn(); DrawPresenceStatus(member, record, venue);
                ImGui.TableNextColumn(); ImGui.TextWrapped(StaffTrackingService.FormatDuration(record.AccruedSeconds));
                ImGui.TableNextColumn();
                var grossPay = PayCalculator.CalculateGrossPay(member, record, venue, config);
                var remainingDue = CalculateDue(member, venue);
                ImGui.TextWrapped($"{remainingDue:N0} {venue.CurrencyLabel}");
                var paymentHelp = $"Gross accrued pay: {grossPay:N0} {venue.CurrencyLabel}\nRecorded payment credit: {record.TotalPaidGil:N0} {venue.CurrencyLabel}\nRemaining due: {remainingDue:N0} {venue.CurrencyLabel}";
                if (config.PayTimeRoundingMinutes > 0
                    && member.PresenceMode != PresenceMode.NoTimer)
                {
                    var payableSeconds =
                        PayCalculator.CalculatePayableSeconds(
                            record.AccruedSeconds,
                            config);
                    paymentHelp +=
                        $"\nPayable time: {StaffTrackingService.FormatDuration(payableSeconds)} " +
                        $"({PayCalculator.DescribeTimeRounding(config)})";
                }
                if (record.LastTradePaymentUtc is { } lastTradeUtc)
                    paymentHelp += $"\nLast detected trade: {record.LastTradePaymentGil:N0} {venue.CurrencyLabel} at {lastTradeUtc.LocalDateTime:g}";
                UiHelpers.Help(paymentHelp);
                ImGui.TableNextColumn();
                var paid = record.Paid;
                if (DrawRosterCheckbox("##paid", ref paid))
                {
                    record.Paid = paid;
                    record.PaidAutomatically = false;
                    record.PaidUtc = paid ? DateTimeOffset.UtcNow : null;
                    record.TotalPaidGil = paid ? Math.Max(record.TotalPaidGil, grossPay) : 0;
                    record.PaidAmount = (float)Math.Min(float.MaxValue, (double)record.TotalPaidGil);
                    if (!paid)
                    {
                        record.LastTradePaymentGil = 0;
                        record.LastTradePaymentUtc = null;
                    }
                    persistence.SaveNow();
                }
                UiHelpers.Help("Manually mark this wage as paid. Clearing the box also clears this night's recorded payment credit so trade payments can be corrected and detected again.");

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Tell")) openTell(member);
                UiHelpers.Help("Open a wrapped message window and send a native /tell to this character and home world.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Target")) targeting.Target(member);
                UiHelpers.Help("Target this staff member if their player actor is currently visible nearby.");
                ImGui.SameLine();
                DrawAttendanceButton(member, record);
                ImGui.SameLine();
                DrawEndShiftButton(record);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private static void DrawPresenceStatus(StaffMember member, NightlyStaffRecord record, VenueProfile venue)
    {
        if (!record.Scheduled) { UiHelpers.Status("Off tonight", ShiftKeeperTheme.Muted); return; }
        if (record.ShiftEndedEarly) { UiHelpers.Status("Shift ended", ShiftKeeperTheme.Amber); return; }
        if (member.PresenceMode == PresenceMode.NoTimer)
        {
            UiHelpers.Status(record.HasWorked ? "Shift complete" : "Awaiting completion", record.HasWorked ? ShiftKeeperTheme.Green : ShiftKeeperTheme.Muted);
            return;
        }
        if (member.PresenceMode == PresenceMode.ManualTimer)
        {
            UiHelpers.Status(record.ManualClockedIn ? "Clocked in" : "Clocked out", record.ManualClockedIn ? ShiftKeeperTheme.Green : ShiftKeeperTheme.Muted);
            return;
        }
        if (record.IsVisible) UiHelpers.Status("In venue", ShiftKeeperTheme.Green);
        else if (record.InCrashGrace) UiHelpers.Status("Crash grace", ShiftKeeperTheme.Amber);
        else UiHelpers.Status("Not visible", ShiftKeeperTheme.Muted);
        var shiftSummary = venue.GetShiftSummary(member);
        if (!string.IsNullOrWhiteSpace(shiftSummary)) ImGui.TextWrapped(shiftSummary);
    }

    private void DrawAttendanceButton(StaffMember member, NightlyStaffRecord record)
    {
        if (record.ShiftEndedEarly)
        {
            ImGui.TextDisabled("Timer stopped");
            return;
        }

        if (member.PresenceMode == PresenceMode.ManualTimer)
        {
            if (ImGui.SmallButton(record.ManualClockedIn ? "Clock Out" : "Clock In"))
            {
                record.ManualClockedIn = !record.ManualClockedIn;
                if (record.ManualClockedIn) record.HasWorked = true;
                persistence.SaveNow();
            }
            UiHelpers.Help("Manual timers are useful for staff who work outside player-detection range, such as shout runners.");
        }
        else if (member.PresenceMode == PresenceMode.NoTimer)
        {
            if (ImGui.SmallButton(record.HasWorked ? "Undo" : "Complete"))
            {
                record.HasWorked = !record.HasWorked;
                persistence.SaveNow();
            }
            UiHelpers.Help("Marks a non-timed shift as completed so its per-shift pay becomes due.");
        }
        else
        {
            ImGui.TextDisabled("Auto");
        }
    }

    private void DrawEndShiftButton(NightlyStaffRecord record)
    {
        var label = record.ShiftEndedEarly ? "Resume Shift" : "End Shift";
        if (ImGui.SmallButton(label))
        {
            record.ShiftEndedEarly = !record.ShiftEndedEarly;
            record.ShiftEndedUtc = record.ShiftEndedEarly ? DateTimeOffset.UtcNow : null;
            record.ManualClockedIn = false;
            record.InCrashGrace = false;
            if (!record.ShiftEndedEarly) record.LastSeenUtc = null;
            persistence.SaveNow();
        }
        UiHelpers.Help(record.ShiftEndedEarly
            ? "Resume this staff member's shift. Manual-timer staff must clock in again; venue-tracked staff resume when visible."
            : "End this staff member's shift early and immediately stop their pay timer. Their accrued time and pay remain saved.");
    }

    private long CalculateDue(StaffMember member, VenueProfile venue) =>
        PayCalculator.CalculateRemainingDue(member, venue.CurrentNight.GetOrCreate(member.Id), venue, config);

    private static bool DrawRosterCheckbox(string id, ref bool value)
    {
        ImGui.PushStyleColor(
            ImGuiCol.FrameBg,
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.SurfaceRaised
                : new Vector4(0.18f, 0.07f, 0.30f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.FrameBgHovered,
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.AccentHover
                : new Vector4(0.35f, 0.14f, 0.58f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.FrameBgActive,
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.Accent
                : new Vector4(0.48f, 0.20f, 0.78f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.98f, 0.92f, 1f, 1f));
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            AirTablet.UI.TabletAppTheme.IsActive
                ? AirTablet.UI.TabletAppTheme.AccentHover
                : new Vector4(0.82f, 0.60f, 1f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, AirTablet.UI.TabletAppTheme.Px(1.5f));
        var changed = ImGui.Checkbox(id, ref value);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        return changed;
    }

    private static float ContentColumnWidth(
        string header,
        IEnumerable<string> values)
    {
        var widest = ImGui.CalcTextSize(header).X;
        foreach (var value in values)
        {
            foreach (var line in value.Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                widest = MathF.Max(
                    widest,
                    ImGui.CalcTextSize(line).X);
            }
        }

        return MathF.Ceiling(
            widest +
            ImGui.GetStyle().CellPadding.X * 2f +
            AirTablet.UI.TabletAppTheme.Px(10f));
    }

    private static string PresenceStatusText(StaffMember member, NightlyStaffRecord record)
    {
        if (!record.Scheduled) return "Off tonight";
        if (record.ShiftEndedEarly) return "Shift ended";
        if (member.PresenceMode == PresenceMode.NoTimer) return record.HasWorked ? "Shift complete" : "Awaiting completion";
        if (member.PresenceMode == PresenceMode.ManualTimer) return record.ManualClockedIn ? "Clocked in" : "Clocked out";
        if (record.IsVisible) return "In venue";
        return record.InCrashGrace ? "Crash grace" : "Not visible";
    }

    private static string AttendanceActionText(StaffMember member, NightlyStaffRecord record)
    {
        if (record.ShiftEndedEarly) return "Timer stopped";
        if (member.PresenceMode == PresenceMode.ManualTimer) return record.ManualClockedIn ? "Clock Out" : "Clock In";
        if (member.PresenceMode == PresenceMode.NoTimer) return record.HasWorked ? "Undo" : "Complete";
        return "Auto";
    }

    private static float ActionRowWidth(StaffMember member, NightlyStaffRecord record)
    {
        var framePadding = ImGui.GetStyle().FramePadding.X * 2f;
        var spacing = ImGui.GetStyle().ItemSpacing.X * 3f;
        var attendance = AttendanceActionText(member, record);
        var endShift = record.ShiftEndedEarly ? "Resume Shift" : "End Shift";
        return MathF.Ceiling(
            ImGui.CalcTextSize("Tell").X + framePadding +
            ImGui.CalcTextSize("Target").X + framePadding +
            ImGui.CalcTextSize(attendance).X + (attendance is "Auto" or "Timer stopped" ? 0f : framePadding) +
            ImGui.CalcTextSize(endShift).X + framePadding + spacing +
            ImGui.GetStyle().CellPadding.X * 2f +
            AirTablet.UI.TabletAppTheme.Px(10f));
    }

    private void DrawStaffList(VenueProfile venue)
    {
        ImGui.BeginChild("staff-list-left", new Vector2(AirTablet.UI.TabletAppTheme.Px(280f), 0), true);
        if (ImGui.Button("Add Targeted Staff", AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 0))))
        {
            AddTargetedStaff(venue);
        }
        UiHelpers.Help("Adds your currently targeted player character using their exact character name and home world.");
        if (ImGui.Button("Add Manually", AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 0))))
        {
            var member = CreateStaffMember("Firstname Lastname", "World", venue.Shifts[0].Id);
            venue.Staff.Add(member);
            venue.CurrentNight.GetOrCreate(member.Id);
            selectedStaffIndex = venue.Staff.Count - 1;
            persistence.SaveNow();
        }
        if (!string.IsNullOrWhiteSpace(staffStatus))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(staffStatus.StartsWith("Added", StringComparison.Ordinal) ? ShiftKeeperTheme.Green : ShiftKeeperTheme.Amber, staffStatus);
            ImGui.PopTextWrapPos();
        }
        ImGui.Separator();
        for (var i = 0; i < venue.Staff.Count; i++)
        {
            var member = venue.Staff[i];
            var label = string.IsNullOrWhiteSpace(member.Name) ? "Unnamed Staff" : member.Name;
            if (ImGui.Selectable($"{label}\n{member.DisplayRole}##staff-{member.Id}", selectedStaffIndex == i)) selectedStaffIndex = i;
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("staff-editor-right", Vector2.Zero, true);
        if (selectedStaffIndex < 0 || selectedStaffIndex >= venue.Staff.Count)
        {
            ImGui.TextWrapped("Select a staff member on the left, or add someone to this venue's permanent staff list.");
            ImGui.EndChild();
            return;
        }
        DrawStaffEditor(venue, venue.Staff[selectedStaffIndex]);
        ImGui.EndChild();
    }

    private void AddTargetedStaff(VenueProfile venue)
    {
        if (!targeting.TryGetCurrentTarget(out var name, out var world))
        {
            staffStatus = targeting.LastStatus;
            return;
        }

        var existingIndex = venue.Staff.FindIndex(x =>
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            x.World.Equals(world, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            selectedStaffIndex = existingIndex;
            staffStatus = $"{name}@{world} is already on this venue's staff list.";
            return;
        }

        var member = CreateStaffMember(name, world, venue.Shifts[0].Id);
        venue.Staff.Add(member);
        venue.CurrentNight.GetOrCreate(member.Id);
        selectedStaffIndex = venue.Staff.Count - 1;
        staffStatus = $"Added {member.TellRecipient}.";
        persistence.SaveNow();
    }

    private void DrawStaffEditor(VenueProfile venue, StaffMember member)
    {
        var changed = false;
        ImGui.TextColored(ShiftKeeperTheme.Purple, "Staff Details");
        var enabled = member.Enabled;
        if (ImGui.Checkbox("Enabled on permanent roster", ref enabled)) { member.Enabled = enabled; changed = true; }
        var name = member.Name;
        if (ImGui.InputText("Character name", ref name, 64)) { member.Name = name; changed = true; }
        var world = member.World;
        if (ImGui.InputText("Home world", ref world, 32)) { member.World = world; changed = true; }
        ImGui.TextDisabled("Use the character's full name and home world so tells and targeting resolve correctly.");

        if (ImGui.BeginCombo("Job role", member.Role))
        {
            foreach (var role in CommonRoles.All)
                if (ImGui.Selectable(role, role == member.Role)) { member.Role = role; changed = true; }
            ImGui.EndCombo();
        }
        if (member.Role == "Other")
        {
            var custom = member.CustomRole;
            if (ImGui.InputText("Custom role", ref custom, 64)) { member.CustomRole = custom; changed = true; }
        }

        venue.NormalizeShiftAssignments(member);
        var assignedShiftIds = member.ShiftIds.ToHashSet();
        var assignedShiftSummary = venue.GetShiftSummary(member);
        var assignedShiftPreview = assignedShiftIds.Count switch
        {
            0 => "None",
            1 => assignedShiftSummary,
            _ => $"{assignedShiftIds.Count} shifts selected",
        };
        if (ImGui.BeginCombo("Assigned shifts", assignedShiftPreview))
        {
            foreach (var option in venue.Shifts)
            {
                var selected = assignedShiftIds.Contains(option.Id);
                if (!ImGui.Selectable($"{option.Name} ({option.RangeText})##assign-{option.Id}", selected, ImGuiSelectableFlags.DontClosePopups))
                    continue;

                if (selected)
                {
                    if (member.ShiftIds.Count > 1) member.ShiftIds.Remove(option.Id);
                }
                else
                {
                    member.ShiftIds.Add(option.Id);
                }
                venue.NormalizeShiftAssignments(member);
                assignedShiftIds = member.ShiftIds.ToHashSet();
                changed = true;
            }
            ImGui.EndCombo();
        }
        if (assignedShiftIds.Count > 1) ImGui.TextWrapped($"Selected: {venue.GetShiftSummary(member)}");
        UiHelpers.Help("Select one or more configured shifts for this staff member. At least one shift remains assigned. Timers count across the combined schedule, and per-shift pay applies once for each selected shift.");

        if (ImGui.BeginCombo("Pay format", member.PayType == PayType.Hourly ? "Hourly" : "Per shift"))
        {
            if (ImGui.Selectable("Hourly", member.PayType == PayType.Hourly)) { member.PayType = PayType.Hourly; changed = true; }
            if (ImGui.Selectable("Per shift", member.PayType == PayType.PerShift)) { member.PayType = PayType.PerShift; changed = true; }
            ImGui.EndCombo();
        }
        var rate = member.PayRate;
        if (ImGui.InputFloat(member.PayType == PayType.Hourly ? "Pay per hour" : "Pay per shift", ref rate, 100_000f, 100_000f, "%.0f")) { member.PayRate = MathF.Max(0, rate); changed = true; }
        UiHelpers.Help("The + and - controls adjust pay by 100,000 at a time. Click the number to type any exact amount, such as 250,000.");

        var presenceLabel = member.PresenceMode switch { PresenceMode.VenueTracking => "Auto: visible in venue", PresenceMode.ManualTimer => "Manual clock in/out", _ => "No timer: complete shift" };
        if (ImGui.BeginCombo("Attendance mode", presenceLabel))
        {
            if (ImGui.Selectable("Auto: visible in venue", member.PresenceMode == PresenceMode.VenueTracking)) { member.PresenceMode = PresenceMode.VenueTracking; changed = true; }
            if (ImGui.Selectable("Manual clock in/out", member.PresenceMode == PresenceMode.ManualTimer)) { member.PresenceMode = PresenceMode.ManualTimer; changed = true; }
            if (ImGui.Selectable("No timer: complete shift", member.PresenceMode == PresenceMode.NoTimer)) { member.PresenceMode = PresenceMode.NoTimer; changed = true; }
            ImGui.EndCombo();
        }
        UiHelpers.Help("Venue tracking counts visible time plus the configured crash grace period. Manual timers suit roaming roles such as shout runners. No timer simply marks a fixed-pay shift complete.");

        var notes = member.Notes;
        ImGui.TextUnformatted("Notes");
        if (ImGui.InputTextMultiline("##staffNotes", ref notes, 512, AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 90)))) { member.Notes = notes; changed = true; }
        if (changed) persistence.SaveNow();

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.12f, 0.20f, 1f));
        if (ImGui.Button("Remove Staff Member"))
        {
            pendingDeleteStaffId = member.Id;
            openDeleteStaffPopup = true;
        }
        ImGui.PopStyleColor();
    }

    private void DrawShiftsAndPay(VenueProfile venue)
    {
        ImGui.BeginChild("shifts-pay-scroll", Vector2.Zero, false);
        UiHelpers.Section("Attendance and pay rules");
        var grace = config.CrashGraceMinutes;
        if (ImGui.SliderInt("Crash grace period", ref grace, 0, 30, "%d min")) { config.CrashGraceMinutes = grace; persistence.SaveNow(); }
        UiHelpers.Help("When venue tracking loses a visible staff actor, their paid timer continues for this long. If they return within the grace period, the time remains continuous; otherwise counting stops when the grace expires.");
        var scheduledOnly = config.TrackOnlyDuringScheduledShift;
        if (ImGui.Checkbox("Only count time inside the assigned shift", ref scheduledOnly)) { config.TrackOnlyDuringScheduledShift = scheduledOnly; persistence.SaveNow(); }
        UiHelpers.Help("Uses your PC's local clock. The venue profile's selected tracking days always apply. When enabled, venue and manual timers also require one of the staff member's assigned shifts to be active.");

        var countUpShiftPay = config.CountUpPerShiftPay;
        if (ImGui.Checkbox("Count per-shift pay up from zero", ref countUpShiftPay)) { config.CountUpPerShiftPay = countUpShiftPay; persistence.SaveNow(); }
        UiHelpers.Help("For timed staff on per-shift pay, calculate current pay proportionally from zero up to the rate for every assigned shift, based on worked time versus their combined schedule. No-timer roles receive the fixed rate for every assigned shift when marked complete.");

        var timeRoundingPresets = new[] { 0, 15, 30, 45, 60 };
        var timeRoundingPreview = config.UseCustomPayTimeRounding
            ? $"Custom: {PayCalculator.DescribeTimeRounding(config)}"
            : config.PayTimeRoundingMinutes <= 0
                ? "No time rounding"
                : PayCalculator.DescribeTimeRounding(config);
        if (ImGui.BeginCombo("Round payable work time", timeRoundingPreview))
        {
            foreach (var minutes in timeRoundingPresets)
            {
                var label = minutes == 0
                    ? "No time rounding"
                    : $"Nearest {minutes} minutes";
                var selected =
                    !config.UseCustomPayTimeRounding
                    && config.PayTimeRoundingMinutes == minutes;
                if (ImGui.Selectable(
                        $"{label}##pay-time-rounding-{minutes}",
                        selected))
                {
                    config.UseCustomPayTimeRounding = false;
                    config.PayTimeRoundingMinutes = minutes;
                    persistence.SaveNow();
                }
            }

            if (ImGui.Selectable(
                    "Custom minutes##pay-time-rounding-custom",
                    config.UseCustomPayTimeRounding))
            {
                config.UseCustomPayTimeRounding = true;
                if (config.PayTimeRoundingMinutes <= 0
                    || timeRoundingPresets.Contains(
                        config.PayTimeRoundingMinutes))
                {
                    config.PayTimeRoundingMinutes = 20;
                }
                persistence.SaveNow();
            }
            ImGui.EndCombo();
        }

        if (config.PayTimeRoundingMinutes > 0)
        {
            var roundingMode =
                PayCalculator.NormalizeTimeRoundingMode(
                    config.PayTimeRoundingMode);
            var roundingModePreview = roundingMode switch
            {
                "Up" => "Always round up",
                "Down" => "Always round down",
                _ => "Round to nearest",
            };
            if (ImGui.BeginCombo(
                    "Payable time direction",
                    roundingModePreview))
            {
                foreach (var (value, label) in new[]
                {
                    ("Nearest", "Round to nearest"),
                    ("Up", "Always round up"),
                    ("Down", "Always round down"),
                })
                {
                    if (ImGui.Selectable(
                            $"{label}##pay-time-direction-{value}",
                            roundingMode == value))
                    {
                        config.PayTimeRoundingMode = value;
                        persistence.SaveNow();
                    }
                }
                ImGui.EndCombo();
            }
        }

        if (config.UseCustomPayTimeRounding)
        {
            var customMinutes = Math.Clamp(
                config.PayTimeRoundingMinutes,
                1,
                1440);
            if (ImGui.InputInt(
                    "Custom rounding minutes",
                    ref customMinutes,
                    1,
                    5))
            {
                config.PayTimeRoundingMinutes = Math.Clamp(
                    customMinutes,
                    1,
                    1440);
                persistence.SaveNow();
            }
        }
        UiHelpers.Help(
            "Round each timed staff member's worked time before calculating hourly or count-up per-shift pay. Nearest uses normal rounding with half intervals rounded up; Always up moves any partial interval to the next interval; Always down removes the partial interval. No-timer fixed shifts are unchanged.");

        var roundingValues = new[] { 0, 10, 100, 1_000, 10_000, 25_000, 50_000, 100_000 };
        var roundingPreview = config.PayRoundingIncrement <= 0 ? "No rounding" : $"Nearest {config.PayRoundingIncrement:N0}";
        if (ImGui.BeginCombo("Round calculated pay", roundingPreview))
        {
            foreach (var increment in roundingValues)
            {
                var label = increment == 0 ? "No rounding" : $"Nearest {increment:N0}";
                if (ImGui.Selectable($"{label}##pay-rounding-{increment}", config.PayRoundingIncrement == increment))
                {
                    config.PayRoundingIncrement = increment;
                    persistence.SaveNow();
                }
            }
            ImGui.EndCombo();
        }
        UiHelpers.Help("Round each calculated wage to the nearest selected amount. This affects hourly and per-shift amounts shown as due and the amount recorded when Paid is checked.");

        var autoTradePayments = config.AutoDetectTradePayments;
        if (ImGui.Checkbox("Apply outgoing gil trades to staff pay", ref autoTradePayments))
        {
            config.AutoDetectTradePayments = autoTradePayments;
            persistence.SaveNow();
        }
        UiHelpers.Help("Watch completed outgoing gil trades and apply the traded amount as payment credit when the recipient matches a scheduled staff member on the active venue roster. Exact home world is used when available; ambiguous same-name matches are ignored. Party membership is not required.");
        ImGui.PushStyleColor(ImGuiCol.Text, ShiftKeeperTheme.Muted);
        ImGui.PushTextWrapPos();
        ImGui.TextWrapped(tradePayments.DetectionStatus);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        var crashRecovery = config.HostCrashRecoveryEnabled;
        if (ImGui.Checkbox("Recover timers after host crash or reload", ref crashRecovery)) { config.HostCrashRecoveryEnabled = crashRecovery; persistence.SaveNow(); }
        UiHelpers.Help("ShiftKeeper saves a tracking checkpoint about every ten seconds. After the plugin or host returns, recovery waits through the main menu, loading screens, and time outside the venue. Previously working staff recover eligible time once their attendance is confirmed, while live visibility and timing continue normally for other staff.");
        if (venue.CurrentNight.LastCrashRecoveryUtc is { } recoveredUtc)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ShiftKeeperTheme.Muted);
            ImGui.TextWrapped($"Last recovery: {StaffTrackingService.FormatDuration(venue.CurrentNight.LastCrashRecoverySeconds)} for {venue.CurrentNight.LastCrashRecoveryStaffCount} staff at {recoveredUtc.LocalDateTime:g}");
            ImGui.PopStyleColor();
        }

        UiHelpers.Section("Configured shifts");
        foreach (var shift in venue.Shifts.ToList())
        {
            ImGui.PushID(shift.Id.ToString());
            ImGui.BeginChild(
                "shift-card",
                new Vector2(-1, AirTablet.UI.TabletAppTheme.Px(142f)),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            var changed = false;
            var name = shift.Name;
            if (ImGui.InputText("Shift name", ref name, 64)) { shift.Name = name; changed = true; }
            var startHour = shift.StartMinutes == 1440 ? 24 : Math.Clamp(shift.StartMinutes / 60, 0, 23);
            var startMinute = shift.StartMinutes == 1440 ? 0 : Math.Clamp(shift.StartMinutes % 60, 0, 59);
            var endHour = shift.EndMinutes == 1440 ? 24 : Math.Clamp(shift.EndMinutes / 60, 0, 23);
            var endMinute = shift.EndMinutes == 1440 ? 0 : Math.Clamp(shift.EndMinutes % 60, 0, 59);
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(110)); if (ImGui.InputInt("Start hour", ref startHour)) changed = true;
            UiHelpers.Help("Use start hour 24 for midnight immediately after the selected venue day, such as the next shift after one ending at 24. Use start hour 0 for midnight at the beginning of the selected day.");
            ImGui.SameLine(); ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(110)); if (ImGui.InputInt("Start minute", ref startMinute)) changed = true;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(110)); if (ImGui.InputInt("End hour", ref endHour)) changed = true;
            ImGui.SameLine(); ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(110)); if (ImGui.InputInt("End minute", ref endMinute)) changed = true;
            if (changed)
            {
                startHour = Math.Clamp(startHour, 0, 24); startMinute = Math.Clamp(startMinute, 0, 59);
                endHour = Math.Clamp(endHour, 0, 24); endMinute = Math.Clamp(endMinute, 0, 59);
                if (startHour == 24) startMinute = 0;
                if (endHour == 24) endMinute = 0;
                shift.StartMinutes = startHour * 60 + startMinute;
                shift.EndMinutes = endHour * 60 + endMinute;
                persistence.SaveNow();
            }
            ImGui.SameLine(); ImGui.TextDisabled(shift.RangeText);
            if (venue.Shifts.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete Shift"))
                {
                    venue.Shifts.Remove(shift);
                    foreach (var staff in venue.Staff)
                    {
                        staff.ShiftIds?.Remove(shift.Id);
                        if (staff.ShiftId == shift.Id) staff.ShiftId = Guid.Empty;
                        venue.NormalizeShiftAssignments(staff);
                    }
                    persistence.SaveNow();
                }
            }
            ImGui.EndChild();
            ImGui.PopID();
        }
        ImGui.InputText("New shift name", ref newShiftName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add Shift"))
        {
            venue.Shifts.Add(new ShiftDefinition { Name = string.IsNullOrWhiteSpace(newShiftName) ? $"Shift {venue.Shifts.Count + 1}" : newShiftName.Trim() });
            newShiftName = string.Empty;
            persistence.SaveNow();
        }
        ImGui.EndChild();
    }

    private static StaffMember CreateStaffMember(string name, string world, Guid initialShiftId) => new()
    {
        Name = name,
        World = world,
        ShiftId = initialShiftId,
        ShiftIds = [initialShiftId],
    };

    private void DrawProfiles(VenueProfile venue)
    {
        ImGui.BeginChild("profiles-scroll", Vector2.Zero, false);
        UiHelpers.Section("Current venue profile");
        var name = venue.Name;
        if (ImGui.InputText("Venue name", ref name, 80)) { venue.Name = name; persistence.SaveNow(); }
        var currency = venue.CurrencyLabel;
        if (ImGui.InputText("Currency label", ref currency, 24)) { venue.CurrencyLabel = currency; persistence.SaveNow(); }

        UiHelpers.Section("Weekly tracking schedule");
        venue.NormalizeTrackingDays();
        ImGui.TextWrapped("Choose the local days when this venue is active. Timers and reload recovery do not add time on unselected days. An overnight configured shift carries its selected start day through the shift's after-midnight portion.");
        if (ImGui.BeginTable("venue-tracking-days", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            foreach (var day in VenueProfile.WeekDayOrder)
            {
                ImGui.TableNextColumn();
                var selected = venue.TrackingDays.Contains(day);
                if (ImGui.Checkbox($"{day}##venue-tracking-{day}", ref selected))
                {
                    if (selected)
                    {
                        if (!venue.TrackingDays.Contains(day)) venue.TrackingDays.Add(day);
                    }
                    else if (venue.TrackingDays.Count > 1)
                    {
                        venue.TrackingDays.Remove(day);
                    }
                    venue.NormalizeTrackingDays();
                    persistence.SaveNow();
                }
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("At least one tracking day must remain selected.");

        UiHelpers.Section("Create and manage profiles");
        ImGui.InputText("New venue name", ref newVenueName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Create Venue"))
        {
            persistence.AddVenue(newVenueName);
            newVenueName = string.Empty;
            selectedStaffIndex = -1;
        }
        if (persistence.Venues.Count > 1)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.12f, 0.20f, 1f));
            if (ImGui.Button("Delete Current Venue")) AirTablet.UI.TabletAppTheme.OpenCenteredModal("Delete Venue?##shift-keeper-delete-venue");
            ImGui.PopStyleColor();
        }

        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Delete Venue?##shift-keeper-delete-venue",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            ImGui.TextWrapped($"Delete '{venue.Name}' and all of its staff, shifts, and night records? This cannot be undone.");
            if (ImGui.Button("Delete", AirTablet.UI.TabletAppTheme.Px(new Vector2(110, 0)))) { persistence.DeleteVenue(venue.Id); selectedStaffIndex = -1; AirTablet.UI.TabletAppTheme.CloseCenteredModal(); }
            ImGui.SameLine(); if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(110, 0)))) AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }

        UiHelpers.Section("Import and export");
        ImGui.TextWrapped("Export saves the complete selected venue profile as JSON. Import creates a separate venue with new internal IDs, so it cannot overwrite an existing profile.");
        var dialogOpenForFrame = dialogs.IsOpen;
        if (dialogOpenForFrame) ImGui.BeginDisabled();
        if (ImGui.Button("Export Current Venue", AirTablet.UI.TabletAppTheme.Px(new Vector2(180, 0))))
        {
            var suggested = UiHelpers.SafeFileName(venue.Name) + "-ShiftKeeper.json";
            dialogs.SaveJson(suggested,
                path => { try { persistence.ExportVenue(venue, path); profileStatus = $"Exported to {path}"; } catch (Exception ex) { profileStatus = ex.Message; } },
                error => profileStatus = error);
        }
        ImGui.SameLine();
        if (ImGui.Button("Import Venue", AirTablet.UI.TabletAppTheme.Px(new Vector2(150, 0))))
        {
            dialogs.OpenJson(
                path => { try { var imported = persistence.ImportVenue(path); profileStatus = $"Imported {imported.Name}."; selectedStaffIndex = -1; } catch (Exception ex) { profileStatus = ex.Message; } },
                error => profileStatus = error);
        }
        if (dialogOpenForFrame) ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(profileStatus)) { ImGui.Spacing(); ImGui.TextWrapped(profileStatus); }

        UiHelpers.Section("Storage");
        ImGui.TextWrapped("Venue profiles, permanent staff lists, and night sessions are saved separately from the small Dalamud settings file.");
        ImGui.TextDisabled(persistence.DataDirectory);
        ImGui.EndChild();
    }

    private void DrawResetConfirmation(VenueProfile venue)
    {
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Reset Venue Night?##shift-keeper-reset-night",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize)) return;
        ImGui.TextWrapped($"Archive the current night for {venue.Name} and start a clean night? Timers and paid statuses will reset.");
        if (ImGui.Button("Reset Night", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0)))) { persistence.ResetNight(venue); AirTablet.UI.TabletAppTheme.CloseCenteredModal(); }
        ImGui.SameLine(); if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(100, 0)))) AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }

    private void DrawDeleteStaffConfirmation(VenueProfile venue)
    {
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Remove Staff?##shift-keeper-remove-staff",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize)) return;
        var member = venue.Staff.FirstOrDefault(x => x.Id == pendingDeleteStaffId);
        ImGui.TextWrapped(member is null ? "Remove this staff member?" : $"Remove {member.Name} from {venue.Name}'s permanent staff list?");
        if (ImGui.Button("Remove", AirTablet.UI.TabletAppTheme.Px(new Vector2(110, 0))))
        {
            venue.Staff.RemoveAll(x => x.Id == pendingDeleteStaffId);
            venue.CurrentNight.Staff.RemoveAll(x => x.StaffId == pendingDeleteStaffId);
            selectedStaffIndex = -1;
            persistence.SaveNow();
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine(); if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(110, 0)))) AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }
}
