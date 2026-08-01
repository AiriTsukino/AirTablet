using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class VenuesTab
{
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly DetectionService detection;
    private string newVenueName = "New Venue";
    private string plotCaptureStatus = string.Empty;

    public VenuesTab(VenueService venues, PersistenceService persistence, DetectionService detection)
    {
        this.venues = venues;
        this.persistence = persistence;
        this.detection = detection;
    }

    public void Draw()
    {
        UiHelpers.Section("Venue profiles");
        ImGui.InputText("New venue name", ref newVenueName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Create venue")) venues.CreateVenue(newVenueName);

        foreach (var venue in venues.Venues.ToArray())
        {
            ImGui.PushID(venue.Id.ToString());
            var name = venue.Name;
            if (ImGui.InputText("Name", ref name, 80)) venues.RenameVenue(venue.Id, name);
            ImGui.SameLine();
            if (ImGui.RadioButton("Active", venues.IsVenueActive && venues.ActiveVenue.Id == venue.Id)) venues.SwitchVenue(venue.Id);
            ImGui.SameLine();
            if (venues.Venues.Count > 1 && ImGui.Button("Delete")) venues.DeleteVenue(venue.Id);
            ImGui.TextDisabled($"Lifetime visitors: {venue.LifetimeVisitors.Count} | Session visitors: {venue.Session.NightlyVisitors.Count} | Blacklisted: {venue.Blacklist.Count}");
            DrawRegionRouting(venue);
            DrawPlotLock(venue);
            DrawCustomRegionMacroRoutes(venue);
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawRegionRouting(VenueProfile venue)
    {
        if (ImGui.TreeNodeEx($"Detection regions##venue-regions-{venue.Id}", ImGuiTreeNodeFlags.None))
        {
            UiHelpers.TextDisabledWrapped("Optional: choose where this venue tracks the visitor list and where each visitor type becomes eligible for greeting. Default uses the whole housing interior indoors, or any enabled custom region outdoors.");

            DrawRegionCombo("Visitor list region", venue.VisitorListRegionId, id => venue.VisitorListRegionId = id,
                "Controls who appears in the visitor list and entry notifications. Default is the whole housing interior indoors, or any enabled custom region outdoors.");
            DrawRegionCombo("First-time greeting region", venue.FirstTimeGreetingRegionId, id => venue.FirstTimeGreetingRegionId = id,
                "First-time visitors are only added to Ungreeted when they enter this region. Default is the classic whole-house behavior.");
            DrawRegionCombo("Returning greeting region", venue.ReturningGreetingRegionId, id => venue.ReturningGreetingRegionId = id,
                "Returning visitors are only added to Ungreeted when they enter this region. Default is the classic whole-house behavior.");
            DrawRegionCombo("VIP greeting region", venue.VipGreetingRegionId, id => venue.VipGreetingRegionId = id,
                "VIP visitors are only added to Ungreeted when they enter this region. Default is the classic whole-house behavior.");

            ImGui.TreePop();
        }
    }

    private void DrawPlotLock(VenueProfile venue)
    {
        if (!ImGui.TreeNodeEx($"Plot-specific safety##plot-lock-{venue.Id}", ImGuiTreeNodeFlags.None))
            return;

        UiHelpers.TextDisabledWrapped("Optional per-venue safety lock. When enabled, AutoGreet only runs this venue while you are on the saved world, housing district, housing type, ward, division, and plot/apartment room. If you leave that saved location, AutoGreet automatically switches the active venue to None so it cannot greet people in the wrong house.");
        ImGui.TextWrapped($"Current location: {detection.CurrentPlotLockStatus}");
        ImGui.TextWrapped($"Saved location: {venue.PlotLock.DisplayText}");

        var enabled = venue.PlotLock.Enabled;
        if (ImGui.Checkbox("Only run this venue on its saved plot", ref enabled))
        {
            venue.PlotLock.Enabled = enabled;
            persistence.SaveNow();
        }

        if (ImGui.Button("Capture current plot"))
        {
            if (detection.TryGetCurrentPlotLock(out var current) && HasCompletePlotLocation(current))
            {
                venue.PlotLock.CopyFrom(current);
                venue.PlotLock.Enabled = true;
                persistence.SaveNow();
                plotCaptureStatus = "Captured the current housing district, ward, division, and plot.";
            }
            else
                plotCaptureStatus = "The game has not exposed the complete housing location yet. Your existing plot lock was not changed; try again after the house finishes loading.";
        }
        UiHelpers.TooltipOnHover("Captures your current world, housing district, house/apartment type, ward, division, plot, and apartment room when the game exposes them. You can still edit the fields manually below.");

        ImGui.SameLine();
        if (ImGui.Button("Clear plot lock"))
        {
            venue.PlotLock = new VenuePlotLock();
            persistence.SaveNow();
            plotCaptureStatus = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(plotCaptureStatus))
            UiHelpers.TextDisabledWrapped(plotCaptureStatus);

        DrawPlotLockManualFields(venue.PlotLock);
        ImGui.TreePop();
    }

    private static bool HasCompletePlotLocation(VenuePlotLock location)
    {
        if (string.IsNullOrWhiteSpace(location.World)
            || string.IsNullOrWhiteSpace(location.HousingDistrict)
            || location.Ward < 0
            || location.Division is not (1 or 2))
            return false;

        return location.IsPlot && location.Plot >= 0
               || location.IsApartment && location.Room > 0;
    }

    private void DrawPlotLockManualFields(VenuePlotLock plotLock)
    {
        var world = plotLock.World;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220));
        if (ImGui.InputText("World", ref world, 32))
        {
            plotLock.World = world.Trim();
            persistence.SaveNow();
        }

        var district = plotLock.HousingDistrict;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(240));
        if (ImGui.InputText("Housing district", ref district, 64))
        {
            plotLock.HousingDistrict = HousingLocationFormatter.NormalizeHousingDistrictName(district);
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Use names like Mist, The Lavender Beds, The Goblet, Shirogane, or Empyreum. Leave blank to allow any housing district.");

        var kindIndex = plotLock.LocationKind.Equals(VenuePlotLock.LocationKindPlot, StringComparison.OrdinalIgnoreCase)
            ? 1
            : plotLock.LocationKind.Equals(VenuePlotLock.LocationKindApartment, StringComparison.OrdinalIgnoreCase)
                ? 2
                : 0;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(180));
        if (ImGui.Combo("Housing type", ref kindIndex, "Any\0Plot\0Apartment\0"))
        {
            plotLock.LocationKind = kindIndex switch
            {
                1 => VenuePlotLock.LocationKindPlot,
                2 => VenuePlotLock.LocationKindApartment,
                _ => VenuePlotLock.LocationKindAny,
            };
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Apartments and plots are treated separately so an apartment room cannot match a regular house plot.");

        var wardDisplay = plotLock.Ward < 0 ? 0 : plotLock.Ward + 1;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(120));
        if (ImGui.InputInt("Ward", ref wardDisplay))
        {
            plotLock.Ward = wardDisplay <= 0 ? -1 : wardDisplay - 1;
            persistence.SaveNow();
        }
        UiHelpers.TooltipOnHover("Use 0 to allow any ward. AutoGreet stores this internally as the game's zero-based ward index.");

        var division = plotLock.Division;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(160));
        if (ImGui.Combo("Division", ref division, "Any\0Main\0Subdivision\0"))
        {
            plotLock.Division = Math.Clamp(division, 0, 2);
            persistence.SaveNow();
        }

        if (plotLock.LocationKind.Equals(VenuePlotLock.LocationKindApartment, StringComparison.OrdinalIgnoreCase))
        {
            var roomDisplay = plotLock.Room < 0 ? 0 : plotLock.Room;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(120));
            if (ImGui.InputInt("Apartment room", ref roomDisplay))
            {
                plotLock.Room = roomDisplay <= 0 ? -1 : roomDisplay;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Use 0 to allow any apartment room in the saved world/district/ward/division.");
        }
        else
        {
            var plotDisplay = plotLock.Plot < 0 ? 0 : plotLock.Plot + 1;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(120));
            if (ImGui.InputInt("Plot", ref plotDisplay))
            {
                plotLock.Plot = plotDisplay <= 0 ? -1 : plotDisplay - 1;
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("Use 0 to allow any plot. AutoGreet stores this internally as the game's zero-based plot index.");
        }
    }

    private void DrawCustomRegionMacroRoutes(VenueProfile venue)
    {
        if (!ImGui.TreeNodeEx($"Extra active macros for custom regions##region-macro-routes-{venue.Id}", ImGuiTreeNodeFlags.None))
            return;

        UiHelpers.TextDisabledWrapped("Use this for things like a beach-party directions region. When someone enters the selected custom region, AutoGreet can send a different active macro once per person for that region without marking them as greeted for the main venue queue.");

        if (ImGui.Button("Add region macro"))
        {
            venue.CustomRegionMacroRoutes.Add(new CustomRegionMacroRoute { Name = "Region macro" });
            venues.RepairVenueData(venue);
            persistence.SaveNow();
        }

        if (venue.CustomRegionMacroRoutes.Count == 0)
        {
            UiHelpers.TextDisabledWrapped("No extra custom-region macros set for this venue.");
            ImGui.TreePop();
            return;
        }

        foreach (var route in venue.CustomRegionMacroRoutes.ToArray())
            DrawCustomRegionMacroRoute(venue, route);

        ImGui.TreePop();
    }

    private void DrawCustomRegionMacroRoute(VenueProfile venue, CustomRegionMacroRoute route)
    {
        ImGui.PushID(route.Id.ToString());
        ImGui.Separator();

        var enabled = route.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            route.Enabled = enabled;
            persistence.SaveNow();
        }

        var name = route.Name;
        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(240));
        if (ImGui.InputText("Name", ref name, 80))
        {
            route.Name = string.IsNullOrWhiteSpace(name) ? "Region macro" : name.Trim();
            persistence.SaveNow();
        }

        DrawRegionCombo("Region", route.RegionId, id => route.RegionId = id,
            "When a visitor enters this custom region, this route's macro can run once for that visitor.");
        DrawMacroCombo(venue, route);

        if (ImGui.Button("Delete region macro"))
        {
            venue.CustomRegionMacroRoutes.Remove(route);
            persistence.SaveNow();
            ImGui.PopID();
            return;
        }

        ImGui.PopID();
    }

    private void DrawMacroCombo(VenueProfile venue, CustomRegionMacroRoute route)
    {
        var profile = venues.GetGreetingProfileForVenue(venue);
        var macros = profile.Macros
            .Where(m => m.Enabled && m.Category != GreetingCategory.Blacklisted)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedName = route.MacroId == Guid.Empty
            ? "None"
            : macros.FirstOrDefault(m => m.Id == route.MacroId) is { } selected
                ? selected.Name
                : "Missing macro";

        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(360));
        if (!ImGui.BeginCombo("Macro", selectedName))
            return;

        if (ImGui.Selectable("None", route.MacroId == Guid.Empty))
        {
            route.MacroId = Guid.Empty;
            persistence.SaveNow();
        }

        foreach (var macro in macros)
        {
            var isSelected = route.MacroId == macro.Id;
            if (ImGui.Selectable($"{macro.Name} ({macro.Category})##macro-{macro.Id}", isSelected))
            {
                route.MacroId = macro.Id;
                persistence.SaveNow();
            }

            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawRegionCombo(string label, Guid selectedId, Action<Guid> setSelected, string tooltip)
    {
        var regions = persistence.CustomRegions
            .OrderBy(r => HousingLocationFormatter.GetTerritoryDisplayName(r.TerritoryType), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedName = selectedId == Guid.Empty
            ? "Default (whole house / outdoor regions)"
            : regions.FirstOrDefault(r => r.Id == selectedId) is { } selected
                ? HousingLocationFormatter.GetRegionDisplayName(selected)
                : "Missing region - using default";

        ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(360));
        if (ImGui.BeginCombo($"{label}##{label}", selectedName))
        {
            if (ImGui.Selectable("Default (whole house / outdoor regions)##default", selectedId == Guid.Empty))
            {
                setSelected(Guid.Empty);
                persistence.SaveNow();
            }

            if (regions.Length > 0)
                ImGui.Separator();

            foreach (var region in regions)
            {
                var regionSelected = selectedId == region.Id;
                var regionLabel = $"{HousingLocationFormatter.GetRegionDisplayName(region)}##region-{region.Id}";
                if (ImGui.Selectable(regionLabel, regionSelected))
                {
                    setSelected(region.Id);
                    persistence.SaveNow();
                }

                if (regionSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        UiHelpers.TooltipOnHover(tooltip);
    }
}
