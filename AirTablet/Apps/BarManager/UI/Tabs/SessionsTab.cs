using BarManager.Models;
using BarManager.Services;
using BarManager.UI.Components;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace BarManager.UI.Tabs;

internal sealed class SessionsTab
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private GambaSessionRecord? pendingDeleteSession;

    public SessionsTab(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
    }

    public void Draw()
    {
        if (!ImGui.BeginChild("##SessionsScroll", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0)), false))
        {
            ImGui.EndChild();
            return;
        }

        var sessions = config.CurrentAudit.GambaSessions;
        UiHelpers.SectionTitle("Saved Gamba Sessions");
        if (sessions.Count == 0)
        {
            UiHelpers.TextMuted("No saved sessions this night.");
            ImGui.EndChild();
            return;
        }

        var totalRolls = sessions.Sum(session => session.Rolls.Count);
        var totalAllowed = sessions.Sum(session => session.RollsAllowed);
        UiHelpers.TextWrappedMuted($"Night total rolls: {totalRolls:N0} resolved out of {totalAllowed:N0} allowed across {sessions.Count:N0} saved session(s).");
        ImGui.Spacing();

        if (ImGui.BeginTable("##sessions", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("Customer");
            ImGui.TableSetupColumn("Drinks");
            ImGui.TableSetupColumn("Rolls");
            ImGui.TableSetupColumn("Payout");
            ImGui.TableSetupColumn("Actions");
            ImGui.TableHeadersRow();

            for (var i = sessions.Count - 1; i >= 0; i--)
            {
                var session = sessions[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(session.StartedAt.ToString("HH:mm"));
                ImGui.TableNextColumn(); ImGui.Text(session.CustomerDisplay);
                ImGui.TableNextColumn(); ImGui.Text(session.DrinksPurchased.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.Text(session.Rolls.Count.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextColored(BarManagerTheme.Green, UiHelpers.Gil(session.TotalPayout));
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                if (ImGui.SmallButton("Details")) ImGui.OpenPopup("details");
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete"))
                    pendingDeleteSession = session;
                if (ImGui.BeginPopup("details"))
                {
                    ImGui.TextColored(BarManagerTheme.Gold, session.CustomerDisplay);
                    foreach (var roll in session.Rolls)
                    {
                        var bonus = string.IsNullOrWhiteSpace(roll.BonusName) || roll.BonusMultiplier <= 1f ? string.Empty : $" ({roll.BonusName} x{roll.BonusMultiplier:0.##})";
                        ImGui.Text($"{roll.Roll} -> {roll.Tier} -> {UiHelpers.Gil(roll.Payout)}{bonus}");
                    }
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        ImGui.EndChild();
        DrawDeleteSessionConfirmation();
    }

    private void DrawDeleteSessionConfirmation()
    {
        if (pendingDeleteSession is null)
            return;

        AirTablet.UI.TabletAppTheme.OpenCenteredModal("Delete saved session?##bar-manager-delete-session");
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Delete saved session?##bar-manager-delete-session",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(390f));
        ImGui.TextUnformatted(
            $"Delete the saved session for {pendingDeleteSession.CustomerDisplay}? This removes its roll history and subtracts {UiHelpers.Gil(pendingDeleteSession.TotalPayout)} from prizes paid out.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button(
                "Delete session",
                AirTablet.UI.TabletAppTheme.Px(new Vector2(130f, 0f))))
        {
            var sessions = config.CurrentAudit.GambaSessions;
            if (sessions.Remove(pendingDeleteSession))
            {
                config.CurrentAudit.PrizesPaidOut = Math.Max(
                    0,
                    config.CurrentAudit.PrizesPaidOut - pendingDeleteSession.TotalPayout);
                persistence.SaveNow();
            }

            pendingDeleteSession = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button(
                "Keep session",
                AirTablet.UI.TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            pendingDeleteSession = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }
}
