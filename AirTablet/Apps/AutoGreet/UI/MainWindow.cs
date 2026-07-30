using System.Diagnostics;
using System.Numerics;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using AutoGreet.UI.Tabs;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI;

public sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly VenueService venueService;
    private readonly MainTab main;
    private readonly VisitorsTab visitorsTab;
    private readonly ActiveVisitorsTab activeVisitorsTab;
    private readonly QueueTab queue;
    private readonly LogTab logTab;
    private readonly DiagnosticLogService logs;
    private readonly DetectionService detectionService;
    private readonly Action openSettings;
    private MainWindowTab selectedTab = MainWindowTab.Main;
    private volatile bool selectLogTabNextDraw;

    private enum MainWindowTab
    {
        Main,
        Greets,
        Visitors,
        Queue,
        Log,
    }

    public MainWindow(Configuration config, VenueService venueService, VisitorService visitorService, QueueService queueService, DetectionService detectionService, PersistenceService persistence, DiagnosticLogService logs, Action openSettings)
        : base("AutoGreet###AutoGreetMainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.config = config;
        this.persistence = persistence;
        this.venueService = venueService;
        this.detectionService = detectionService;
        this.openSettings = openSettings;
        this.logs = logs;
        this.logs.LogAdded += SelectLogTab;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new System.Numerics.Vector2(780, 540)),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue)
        };
        main = new MainTab(config, venueService, visitorService, queueService, detectionService, persistence, openSettings);
        visitorsTab = new VisitorsTab(venueService, visitorService, queueService);
        activeVisitorsTab = new ActiveVisitorsTab(config, venueService, visitorService, queueService, detectionService, persistence);
        queue = new QueueTab(venueService);
        logTab = new LogTab(logs);
    }

    public override void PreDraw() => AutoGreetTheme.Push();

    public override void PostDraw() => AutoGreetTheme.Pop();

    public override void Draw()
    {
        if (selectLogTabNextDraw)
        {
            selectedTab = MainWindowTab.Log;
            selectLogTabNextDraw = false;
        }

        // Use a small manual tab row instead of ImGui TabBar items. The visible
        // counts can change whenever visitors enter/leave, but this selectedTab
        // value only changes when the user clicks a tab, so live counts no
        // longer make ImGui restore Main.
        var venue = venueService.ActiveVenueOrNull;
        var session = venue?.Session;
        var ungreetedCount = session?.Ungreeted.Count ?? 0;
        var greetedCount = session?.Greeted.Count ?? 0;
        var activeVisitorCount = 0;
        if (venue is not null && session is not null)
        {
            activeVisitorCount = session.NightlyVisitors.Count(v => v.Present && (config.ShowBlacklistedInActiveVisitors || !venue.Blacklist.Contains(v.Key.ToString())));
        }
        else if (config.MonitorWhenNoVenueSelected)
        {
            activeVisitorCount = detectionService.PresentKeys.Count;
        }
        var queueCount = venue?.Queue.Count(q => q.Status == Models.QueueEntryStatus.Waiting) ?? 0;
        var logCount = logs.Entries.Count(e => e.Severity == Models.MacroLogSeverity.Error || e.Severity == Models.MacroLogSeverity.Warning);

        var tabRowScreenPos = ImGui.GetCursorScreenPos();

        DrawTabButton("Main##autogreet-tab-main", MainWindowTab.Main);
        ImGui.SameLine();
        DrawTabButton($"Greets ({ungreetedCount}/{greetedCount})##autogreet-tab-greets", MainWindowTab.Greets);
        ImGui.SameLine();
        DrawTabButton($"Visitors ({activeVisitorCount})##autogreet-tab-visitors", MainWindowTab.Visitors);
        ImGui.SameLine();
        DrawTabButton($"Queue ({queueCount})##autogreet-tab-queue", MainWindowTab.Queue);
        ImGui.SameLine();
        DrawTabButton($"Log ({logCount})##autogreet-tab-log", MainWindowTab.Log);

        DrawTopRightButtonsOnTabRow(tabRowScreenPos);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.BeginChild(
                "##autogreet-tab-content",
                Vector2.Zero,
                false,
                ImGuiWindowFlags.None))
        {
            switch (selectedTab)
            {
                case MainWindowTab.Greets:
                    visitorsTab.Draw();
                    break;
                case MainWindowTab.Visitors:
                    activeVisitorsTab.Draw();
                    break;
                case MainWindowTab.Queue:
                    queue.Draw();
                    break;
                case MainWindowTab.Log:
                    logTab.Draw();
                    break;
                case MainWindowTab.Main:
                default:
                    main.Draw();
                    break;
            }
        }
        ImGui.EndChild();
    }

    private void SelectLogTab()
    {
        var newest = logs.Entries.FirstOrDefault();
        if (newest is not null && newest.Severity is Models.MacroLogSeverity.Warning or Models.MacroLogSeverity.Error)
            selectLogTabNextDraw = true;
    }

    private void DrawTabButton(string label, MainWindowTab tab)
    {
        var selected = selectedTab == tab;
        var displayLabel = label.Split("##", StringSplitOptions.None)[0];
        var width = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(76f),
            ImGui.CalcTextSize(displayLabel).X + AirTablet.UI.TabletAppTheme.Px(26f));

        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, AutoGreetTheme.Purple);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AutoGreetTheme.PurpleHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AutoGreetTheme.PurpleActive);
        }

        if (ImGui.Button(label, new Vector2(width, AirTablet.UI.TabletAppTheme.Px(34f))))
            selectedTab = tab;

        if (selected)
            ImGui.PopStyleColor(3);
    }

    private void DrawTopRightButtonsOnTabRow(Vector2 tabBarScreenPos)
    {
        const string settingsLabel = "Settings##autogreet-main-top-settings";
        var rightMargin = AirTablet.UI.TabletAppTheme.Px(12f);
        const float topInset = 0f;
        var buttonHeight = AirTablet.UI.TabletAppTheme.Px(34f);
        var settingsWidth = AirTablet.UI.TabletAppTheme.Px(94f);

        var contentMax = ImGui.GetWindowContentRegionMax();
        var windowPos = ImGui.GetWindowPos();
        var settingsPos = new Vector2(
            windowPos.X + contentMax.X - settingsWidth - rightMargin,
            tabBarScreenPos.Y + topInset);

        var savedCursor = ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(settingsPos);
        if (ImGui.Button(settingsLabel, new Vector2(settingsWidth, buttonHeight)))
            openSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open AutoGreet settings");

        ImGui.SetCursorScreenPos(savedCursor);
    }

}
