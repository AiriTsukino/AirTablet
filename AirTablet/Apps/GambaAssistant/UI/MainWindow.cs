using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Games.DeathRoll;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;
using GambaAssistant.UI.Tabs;
using GambaAssistant.UI.Tabs.SettingsTabs;

namespace GambaAssistant.UI;

public sealed class MainWindow : Window
{
    private readonly TableTab table;
    private readonly PlayersTab players;
    private readonly DealerLedgerTab ledger;
    private readonly RulesTab rules;
    private readonly TradeMonitorTab trades;
    private readonly HistoryExportTab history;
    private readonly LogTerminalTab logTab;
    private readonly DemoModeTab demo;
    private readonly OverlaySettingsTab overlaySettings;
    private readonly DeathRollTournamentTab deathRoll;
    private readonly Action openSettings;
    private readonly Configuration config;
    private int selectedTab;
    private int selectedBlackjackTab;
    private int selectedDrtTab;

    public MainWindow(Configuration config, BlackjackSession session, ProfileService profiles, PartyService party, PlayerSessionService playerService, DealerLedgerService ledgerService, TradeMonitorService tradeMonitor, DiceService dice, ChatQueueService chat, OverlayService overlays, UndoService undo, DemoModeService demoMode, ExportService exports, DeathRollTournamentService deathRollService, LogService log, Action openSettings, Action<bool> setDrtBracketWindowOpen)
        : base("GambaAssistant###GambaAssistantMainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.openSettings = openSettings;
        this.config = config;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(900, 620)), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        table = new TableTab(config, session, profiles, party, playerService, ledgerService, dice, chat, overlays, undo, log);
        players = new PlayersTab(session, playerService, ledgerService);
        ledger = new DealerLedgerTab(ledgerService);
        rules = new RulesTab(session, profiles);
        trades = new TradeMonitorTab(session, tradeMonitor);
        history = new HistoryExportTab(session, exports);
        logTab = new LogTerminalTab(log);
        demo = new DemoModeTab(config, demoMode, log);
        overlaySettings = new OverlaySettingsTab(config, session);
        deathRoll = new DeathRollTournamentTab(config, deathRollService, chat, log, setDrtBracketWindowOpen);
    }

    public override void PreDraw() => GambaTheme.Push();
    public override void PostDraw() => GambaTheme.Pop();

    public override void Draw()
    {
        DrawHeader();
        ImGui.Dummy(AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 5f)));

        var navWidth = CalculateNavigationWidth();
        ImGui.BeginChild(
            "##GambaMainNav",
            new Vector2(navWidth, 0),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawSidebarNavItem(0, "Blackjack");
        DrawSidebarNavItem(1, "DRT");
        DrawSidebarNavItem(2, "Log / Terminal");

        ImGui.Dummy(AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 14f)));
        DrawActiveSectionNavigation();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild(
            "##GambaMainContent",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar);
        DrawMainPanel();
        ImGui.EndChild();
    }

    private static float CalculateNavigationWidth()
    {
        string[] labels =
        [
            "Blackjack",
            "DRT",
            "Log / Terminal",
            "Players & Banks",
            "Dealer Ledger",
            "Trade Monitor",
            "History / Export",
            "Demo / Test",
            "Tournament",
            "Bracket",
            "Settings",
        ];
        var widest = labels.Max(label => ImGui.CalcTextSize(label).X);
        return Math.Clamp(
            widest + ImGui.GetStyle().FramePadding.X * 2f +
            ImGui.GetStyle().WindowPadding.X * 2f +
            AirTablet.UI.TabletAppTheme.Px(14f),
            AirTablet.UI.TabletAppTheme.Px(132f),
            AirTablet.UI.TabletAppTheme.Px(220f));
    }

    private void DrawSidebarNavItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(
                $"{label}##gamba-primary-{index}",
                selectedTab == index,
                new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(34f))))
        {
            selectedTab = index;
        }
    }

    private void DrawActiveSectionNavigation()
    {
        if (selectedTab == 0)
        {
            ImGui.TextColored(GambaTheme.Gold, "Blackjack");
            DrawBlackjackSidebarItem(0, "Table");
            DrawBlackjackSidebarItem(1, "Players & Banks");
            DrawBlackjackSidebarItem(2, "Dealer Ledger");
            DrawBlackjackSidebarItem(3, "Rules");
            DrawBlackjackSidebarItem(4, "Trade Monitor");
            DrawBlackjackSidebarItem(5, "History / Export");
            DrawBlackjackSidebarItem(6, "Overlay");
            DrawBlackjackSidebarItem(7, "Demo / Test");
        }
        else if (selectedTab == 1)
        {
            ImGui.TextColored(GambaTheme.Gold, "DRT");
            DrawDrtSidebarItem(0, "Tournament");
            DrawDrtSidebarItem(1, "Bracket");
            DrawDrtSidebarItem(2, "Log");
            DrawDrtSidebarItem(3, "Settings");
        }
    }

    private void DrawBlackjackSidebarItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(
                $"{label}##gamba-blackjack-{index}",
                selectedBlackjackTab == index,
                new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(28f))))
        {
            selectedBlackjackTab = index;
        }
    }

    private void DrawDrtSidebarItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(
                $"{label}##gamba-drt-{index}",
                selectedDrtTab == index,
                new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(28f))))
        {
            selectedDrtTab = index;
        }
    }

    private void DrawMainPanel()
    {
        var panelSize = ImGui.GetContentRegionAvail();

        switch (selectedTab)
        {
            case 0: UiHelpers.Panel($"Blackjack - {GetBlackjackTabName(selectedBlackjackTab)}", DrawSelectedBlackjackTab, panelSize); break;
            case 1: UiHelpers.Panel($"DRT - {GetDrtTabName(selectedDrtTab)}", DrawSelectedDrtTab, panelSize); break;
            case 2: UiHelpers.Panel("Log / Terminal", logTab.Draw, panelSize); break;
        }
    }

    private void DrawSelectedBlackjackTab()
    {
        switch (selectedBlackjackTab)
        {
            case 0: table.Draw(); break;
            case 1: players.Draw(); break;
            case 2: ledger.Draw(); break;
            case 3: rules.Draw(); break;
            case 4: trades.Draw(); break;
            case 5: history.Draw(); break;
            case 6: overlaySettings.Draw(); break;
            case 7: demo.Draw(); break;
        }
    }

    private void DrawSelectedDrtTab()
    {
        switch (selectedDrtTab)
        {
            case 0: deathRoll.Draw(); break;
            case 1: deathRoll.DrawBracketTab(); break;
            case 2: deathRoll.DrawLogTab(); break;
            case 3: deathRoll.DrawSettingsTab(); break;
        }
    }

    public void DrawDrtBracketWindow()
    {
        deathRoll.DrawDetachedBracket();
    }

    private void DrawHeader()
    {
        if (ImGui.BeginTable(
                "##gamba-toolbar",
                2,
                ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("overlay", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(
                "settings",
                ImGuiTableColumnFlags.WidthFixed,
                AirTablet.UI.TabletAppTheme.Px(104f));
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var overlayEnabled = config.Overlay.Enabled;
            if (ImGui.Checkbox("Enable overlay##gamba-top-enable-overlay", ref overlayEnabled))
                config.Overlay.Enabled = overlayEnabled;
            UiHelpers.Tooltip("Turns the Blackjack overlay window on or off.");

            ImGui.SameLine(0, AirTablet.UI.TabletAppTheme.Px(18f));
            var compactOverlay = config.Overlay.Compact;
            if (ImGui.Checkbox("Compact overlay##gamba-top-compact-overlay", ref compactOverlay))
                config.Overlay.Compact = compactOverlay;
            UiHelpers.Tooltip("Uses the smaller Blackjack overlay layout for crowded screens.");

            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Settings##gamba-main-top-settings",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                openSettings();
            }
            UiHelpers.Tooltip("Open GambaAssistant settings.");
            ImGui.EndTable();
        }
        ImGui.Separator();
    }

    private void DrawPrimaryNavigation()
    {
        DrawNavigationStrip(
            "##gamba-primary-nav",
            ["Blackjack", "Death Roll Tournament", "Log / Terminal"],
            selectedTab,
            index => selectedTab = index);
    }

    private void DrawSecondaryNavigation()
    {
        if (selectedTab == 0)
        {
            DrawNavigationStrip(
                "##gamba-blackjack-nav",
                ["Table", "Players", "Ledger", "Rules", "Trades", "History", "Overlay", "Demo"],
                selectedBlackjackTab,
                index => selectedBlackjackTab = index,
                AirTablet.UI.TabletAppTheme.Px(28f));
        }
        else if (selectedTab == 1)
        {
            DrawNavigationStrip(
                "##gamba-drt-nav",
                ["Tournament", "Bracket", "Log", "Settings"],
                selectedDrtTab,
                index => selectedDrtTab = index,
                AirTablet.UI.TabletAppTheme.Px(28f));
        }
    }

    private static void DrawNavigationStrip(
        string id,
        IReadOnlyList<string> labels,
        int selected,
        Action<int> select,
        float? height = null)
    {
        if (!ImGui.BeginTable(id, labels.Count, ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        for (var index = 0; index < labels.Count; index++)
        {
            ImGui.TableNextColumn();
            if (UiHelpers.VerticalNavItem(
                    $"{labels[index]}##{id}-{index}",
                    selected == index,
                    new Vector2(-1f, height ?? AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                select(index);
            }
        }
        ImGui.EndTable();
    }

    private static string GetBlackjackTabName(int index) => index switch
    {
        0 => "Table",
        1 => "Players & Banks",
        2 => "Dealer Ledger",
        3 => "Rules",
        4 => "Trade Monitor",
        5 => "History / Export",
        6 => "Overlay",
        7 => "Demo / Test",
        _ => "Table",
    };

    private static string GetDrtTabName(int index) => index switch
    {
        0 => "Tournament",
        1 => "Bracket",
        2 => "Log",
        3 => "Settings",
        _ => "Tournament",
    };

}
