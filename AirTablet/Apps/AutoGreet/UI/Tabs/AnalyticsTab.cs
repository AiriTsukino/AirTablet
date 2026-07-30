using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class AnalyticsTab
{
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private bool confirmReset;

    public AnalyticsTab(VenueService venues, VisitorService visitors)
    {
        this.venues = venues;
        this.visitors = visitors;
    }

    public void Draw()
    {
        var venue = venues.ActiveVenue;
        var session = venue.Session;
        UiHelpers.Section("Current session");
        ImGui.Text($"Started: {session.StartedUtc.LocalDateTime:g}");
        ImGui.Text($"Total unique lifetime visitors: {venue.LifetimeVisitors.Count}");
        ImGui.Text($"Night/session visitors: {session.NightlyVisitors.Count}");
        ImGui.Text($"Greeted: {session.Greeted.Count}");
        ImGui.Text($"Ungreeted: {session.Ungreeted.Count}");
        ImGui.Text($"Skipped: {session.Skipped.Count}");

        if (ImGui.Button("Save Nightly Count")) visitors.SaveNightlySnapshot();
        ImGui.SameLine();
        if (ImGui.Button(confirmReset ? "Confirm Reset Session" : "Reset Session"))
        {
            if (confirmReset) { visitors.ResetSession(); confirmReset = false; }
            else confirmReset = true;
        }
        if (confirmReset)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel reset")) confirmReset = false;
        }

        UiHelpers.Section("Saved snapshots");
        if (ImGui.BeginTable("snapshots", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Saved");
            ImGui.TableSetupColumn("Visitors");
            ImGui.TableSetupColumn("Greeted");
            ImGui.TableSetupColumn("Ungreeted");
            ImGui.TableHeadersRow();
            foreach (var snap in session.Snapshots.OrderByDescending(s => s.SavedAtUtc))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(snap.SavedAtUtc.LocalDateTime.ToString("g"));
                ImGui.TableNextColumn(); ImGui.Text(snap.TotalVisitors.ToString());
                ImGui.TableNextColumn(); ImGui.Text(snap.GreetedCount.ToString());
                ImGui.TableNextColumn(); ImGui.Text(snap.UngreetedCount.ToString());
            }
            ImGui.EndTable();
        }
    }
}
