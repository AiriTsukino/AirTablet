using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class QueueTab
{
    private readonly VenueService venues;
    public QueueTab(VenueService venues) => this.venues = venues;

    public void Draw()
    {
        UiHelpers.Section("Queue");
        if (!venues.IsVenueActive)
        {
            UiHelpers.TextDisabledWrapped("No active venue is selected. Queue processing is paused.");
            return;
        }
        if (ImGui.BeginTable("queueTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Visitor");
            ImGui.TableSetupColumn("Category");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Enqueued");
            ImGui.TableSetupColumn("Details");
            ImGui.TableHeadersRow();
            foreach (var q in venues.ActiveVenue.Queue.OrderByDescending(q => q.EnqueuedUtc))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(q.Visitor.Display);
                ImGui.TableNextColumn(); ImGui.Text(q.Category.ToString());
                ImGui.TableNextColumn(); ImGui.Text(q.Status.ToString());
                ImGui.TableNextColumn(); ImGui.Text(q.EnqueuedUtc.LocalDateTime.ToString("g"));
                ImGui.TableNextColumn(); ImGui.TextWrapped(q.StatusText);
            }
            ImGui.EndTable();
        }
    }
}
