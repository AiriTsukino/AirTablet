using System.Numerics;
using BarManager.Services;
using BarManager.UI.Components;
using BarManager.UI.Tabs;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace BarManager.UI;

internal sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly Action openSettings;
    private readonly AuditTab auditTab;
    private readonly GambaDrinkTab gambaTab;
    private readonly SessionsTab sessionsTab;
    private readonly ReportTab reportTab;

    public MainWindow(Configuration config, PersistenceService persistence, Action openSettings)
        : base("BarManager###BarManagerMain")
    {
        Size = AirTablet.UI.TabletAppTheme.Px(new Vector2(980, 720));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(900, 620)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.config = config;
        this.persistence = persistence;
        this.openSettings = openSettings;
        auditTab = new AuditTab(config, persistence);
        gambaTab = new GambaDrinkTab(config, persistence);
        sessionsTab = new SessionsTab(config, persistence);
        reportTab = new ReportTab(config, persistence);
    }

    public void Dispose() => gambaTab.Dispose();

    public override void PreDraw() => BarManagerTheme.Push();
    public override void PostDraw() => BarManagerTheme.Pop();

    public override void Draw()
    {
        if (ImGui.BeginTable("##bar-manager-toolbar", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("context", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(
                "settings",
                ImGuiTableColumnFlags.WidthFixed,
                AirTablet.UI.TabletAppTheme.Px(104f));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var venues = config.Venues.OrderBy(venue => venue.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var activeIndex = Math.Max(0, Array.FindIndex(venues, venue => venue.Id == config.ActiveVenueId));
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(220f));
            if (gambaTab.HasActiveSession) ImGui.BeginDisabled();
            if (ImGui.Combo("##bar-manager-active-profile", ref activeIndex, venues.Select(venue => venue.Name).ToArray(), venues.Length) && activeIndex < venues.Length)
            {
                config.ActiveVenueId = venues[activeIndex].Id;
                persistence.SaveNow();
            }
            if (gambaTab.HasActiveSession) ImGui.EndDisabled();
            UiHelpers.TooltipOnHover(gambaTab.HasActiveSession
                ? "Venue profile switching is locked while a gamba session is active."
                : "Select the active BarManager venue profile.");
            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Settings",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                openSettings();
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        if (ImGui.BeginTabBar("##BarManagerTabs"))
        {
            if (ImGui.BeginTabItem("Audit")) { auditTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(config.ActiveVenue.Gamba.DrinkName)) { gambaTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Sessions")) { sessionsTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Report")) { reportTab.Draw(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }
}
