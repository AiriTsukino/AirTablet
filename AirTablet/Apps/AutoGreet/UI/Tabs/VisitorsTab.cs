using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class VisitorsTab
{
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly QueueService queue;

    public VisitorsTab(VenueService venues, VisitorService visitors, QueueService queue)
    {
        this.venues = venues;
        this.visitors = visitors;
        this.queue = queue;
    }

    public void Draw()
    {
        if (!venues.IsVenueActive)
        {
            UiHelpers.TextDisabledWrapped("No active venue is selected. Visitor tracking is paused.");
            return;
        }

        var session = venues.ActiveVenue.Session;
        if (ImGui.BeginTable("visitors-combined-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn($"Ungreeted ({session.Ungreeted.Count})");
            ImGui.TableSetupColumn($"Greeted ({session.Greeted.Count})");
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawList("Ungreeted", session.Ungreeted.ToArray(), greetedList: false);

            ImGui.TableSetColumnIndex(1);
            DrawList("Greeted", session.Greeted.ToArray(), greetedList: true);

            ImGui.EndTable();
        }
    }

    private void DrawList(string label, IReadOnlyList<VisitorKey> list, bool greetedList)
    {
        var height = Math.Max(
            AirTablet.UI.TabletAppTheme.Px(240f),
            ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(8f));
        if (ImGui.BeginChild($"{label}-combined-child", new System.Numerics.Vector2(0, height), true))
        {
            for (var i = 0; i < list.Count; i++)
            {
                DrawVisitorActions(list[i], greetedList, i);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }

    private void DrawVisitorActions(VisitorKey key, bool greetedList, int index)
    {
        ImGui.PushID($"combined-{(greetedList ? "g" : "u")}-{index}-{key}");
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
