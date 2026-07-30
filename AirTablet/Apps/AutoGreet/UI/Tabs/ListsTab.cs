using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class ListsTab
{
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly QueueService queue;
    private readonly Func<SessionData, IReadOnlyList<VisitorKey>> selector;
    private readonly string title;

    public ListsTab(string title, VenueService venues, VisitorService visitors, QueueService queue, Func<SessionData, IReadOnlyList<VisitorKey>> selector)
    {
        this.title = title;
        this.venues = venues;
        this.visitors = visitors;
        this.queue = queue;
        this.selector = selector;
    }

    public void Draw()
    {
        UiHelpers.Section(title);
        var list = selector(venues.ActiveVenue.Session).ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            var key = list[i];
            ImGui.PushID($"{title}-{i}-{key}");
            var state = venues.ActiveVenue.Session.NightlyVisitors.FirstOrDefault(v => string.Equals(v.Key.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));
            UiHelpers.VisitorRow(key, state?.Present == true, state?.ReturningThisSession == true, state?.HereWhenArrived == true);
            ImGui.SameLine();
            if (!title.Contains("Greeted", StringComparison.OrdinalIgnoreCase) && ImGui.SmallButton("Greet Now")) queue.Enqueue(key, forceStart: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Mark Greeted")) visitors.MarkGreeted(key);
            ImGui.SameLine();
            if (ImGui.SmallButton("Ungreeted")) visitors.MoveToUngreeted(key);
            ImGui.SameLine();
            if (ImGui.SmallButton("Skip")) visitors.Skip(key);
            ImGui.PopID();
            ImGui.Separator();
        }
    }
}
