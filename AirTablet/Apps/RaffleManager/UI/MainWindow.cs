using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RaffleManager.Models;
using RaffleManager.Services;
using RaffleManager.UI.Components;

namespace RaffleManager.UI;

internal sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly RaffleService raffle;
    private readonly SoundService sound;
    private readonly LogoService logo;
    private readonly AnnouncementService announcements;
    private readonly Action openSettings;
    private readonly Stopwatch spinWatch = new();

    private string nameInput = string.Empty;
    private string worldInput = string.Empty;
    private int ticketsInput = 1;
    private bool vipFreeTicket;
    private bool addBogoBonusTickets;
    private bool spinning;
    private double nextTickSeconds;
    private string displayName = "Ready";
    private bool displayFlip;
    private WinnerRecord? winnerPopup;
    private Guid? pendingDelete;
    private bool pendingClearWinnerHistory;
    private bool pendingWinnerPick;

    public MainWindow(Configuration config, PersistenceService persistence, RaffleService raffle, SoundService sound, LogoService logo, AnnouncementService announcements, Action openSettings)
        : base("RaffleManager###RaffleManagerMain")
    {
        Size = AirTablet.UI.TabletAppTheme.Px(new Vector2(1160, 740));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = AirTablet.UI.TabletAppTheme.Px(new Vector2(980, 620)),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.config = config;
        this.persistence = persistence;
        this.raffle = raffle;
        this.sound = sound;
        this.logo = logo;
        this.announcements = announcements;
        this.openSettings = openSettings;
    }

    private VenueProfile Profile => config.Profile;
    private RaffleState Data => Profile.Data;

    public override void PreDraw() => RaffleTheme.Push();
    public override void PostDraw() => RaffleTheme.Pop();
    public void Dispose() { }

    public override void Draw()
    {
        UpdateSpinAnimation();
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##mainTabs"))
        {
            if (ImGui.BeginTabItem("Raffle"))
            {
                DrawRaffleTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("History"))
            {
                DrawHistoryTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawWinnerPopup();
        DrawPickWinnerConfirmation();
        DrawDeleteConfirmation();
        DrawClearHistoryConfirmation();
    }

    private void DrawRaffleTab()
    {
        var avail = ImGui.GetContentRegionAvail();
        var splitterWidth = AirTablet.UI.TabletAppTheme.Px(8f);
        var layoutWidth = MathF.Max(1f, avail.X - splitterWidth);
        var leftWidth = GetProfileLeftPanelWidth(layoutWidth);

        if (UiHelpers.BeginCard(
                "##raffle-left-column",
                new Vector2(leftWidth, avail.Y)))
        {
            DrawAddCard();
            ImGui.Spacing();
            DrawContestantsCard();
        }
        UiHelpers.EndCard();

        ImGui.SameLine(0, 0);
        DrawSplitter(splitterWidth, avail.Y);
        ImGui.SameLine(0, 0);
        if (UiHelpers.BeginCard(
                "##raffle-randomizer-column",
                new Vector2(0, avail.Y)))
        {
            DrawRandomizerCard();
        }
        UiHelpers.EndCard();
    }

    private void DrawHistoryTab()
    {
        var records = raffle.WinnerHistory
            .OrderByDescending(w => w.PulledAt)
            .ToList();

        UiHelpers.Header("Winner History", $"{records.Count:N0} completed raffle pull(s) for profile '{Profile.Name}'.");
        UiHelpers.TextMutedWrapped("History is saved per venue profile and records winner details at the moment the raffle is pulled.");
        ImGui.Spacing();

        if (records.Count > 0)
        {
            if (ImGui.Button("Delete History"))
                pendingClearWinnerHistory = true;
            UiHelpers.TooltipOnHover("Deletes the saved winner history for only the active venue profile. Current contestants are not removed.");
        }

        ImGui.Spacing();

        if (records.Count == 0)
        {
            if (UiHelpers.BeginCard(
                    "##emptyHistory",
                    AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 118f)),
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.TextColored(RaffleTheme.Teal, "No previous winners yet.");
                UiHelpers.TextMutedWrapped("After you pull a winner from the Raffle tab, the result will be saved here with the winner, tickets, draw size, and date.");
            }
            UiHelpers.EndCard();
            return;
        }

        var tableHeight = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(160f),
            ImGui.GetContentRegionAvail().Y);
        if (ImGui.BeginTable("##winnerHistory", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(150f));
            ImGui.TableSetupColumn("Winner", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("Tickets", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(82f));
            ImGui.TableSetupColumn("Draw Tickets", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(105f));
            ImGui.TableSetupColumn("Participants", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(96f));
            ImGui.TableHeadersRow();

            foreach (var record in records)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); UiHelpers.ClippedTextWithTooltip(record.PulledAt.ToString("yyyy-MM-dd HH:mm"));
                ImGui.TableNextColumn(); UiHelpers.ClippedTextWithTooltip(record.Name);
                ImGui.TableNextColumn(); UiHelpers.ClippedTextWithTooltip(string.IsNullOrWhiteSpace(record.World) ? "—" : record.World);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.Tickets.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.TotalTickets.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.TotalParticipants.ToString("N0"));
            }

            ImGui.EndTable();
        }
    }

    private float GetProfileLeftPanelWidth(float layoutWidth)
    {
        var minLeft = AirTablet.UI.TabletAppTheme.Px(380f);
        var minRight = AirTablet.UI.TabletAppTheme.Px(420f);

        if (layoutWidth <= minLeft + minRight)
            return MathF.Max(minLeft, layoutWidth * 0.40f);

        var ratio = Profile.MainWindowLeftPanelRatio <= 0f ? 0.33f : Profile.MainWindowLeftPanelRatio;
        return Math.Clamp(layoutWidth * ratio, minLeft, layoutWidth - minRight);
    }

    private void DrawSplitter(float width, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, RaffleTheme.Border);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RaffleTheme.Pink);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, RaffleTheme.Teal);
        ImGui.Button("##leftRightSplitter", new Vector2(width, height));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemActive())
        {
            var contentWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
            var layoutWidth = MathF.Max(1f, contentWidth - width);
            var currentLeftWidth = GetProfileLeftPanelWidth(layoutWidth);
            var minLeft = AirTablet.UI.TabletAppTheme.Px(380f);
            var minRight = AirTablet.UI.TabletAppTheme.Px(420f);
            var newLeftWidth = Math.Clamp(
                currentLeftWidth + ImGui.GetIO().MouseDelta.X,
                minLeft,
                MathF.Max(minLeft, layoutWidth - minRight));
            Profile.MainWindowLeftPanelRatio = Math.Clamp(newLeftWidth / layoutWidth, 0.20f, 0.80f);
        }

        if (ImGui.IsItemDeactivated())
            persistence.SaveData();
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
    }

    private void DrawHeader()
    {
        if (ImGui.BeginTable("##raffle-toolbar", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("context", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(
                "settings",
                ImGuiTableColumnFlags.WidthFixed,
                AirTablet.UI.TabletAppTheme.Px(104f));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(RaffleTheme.Pink, Profile.VenueName);
            ImGui.SameLine();
            UiHelpers.TextMuted($"Profile: {Profile.Name}");
            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Settings",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                openSettings();
            }
            ImGui.EndTable();
        }
    }

    private void DrawAddCard()
    {
        ImGui.TextColored(RaffleTheme.Pink, "Add Contestant");
        ImGui.Separator();

        if (ImGui.BeginTable(
                "##contestant-identity",
                2,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##name", "Character name", ref nameInput, 64);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##world", "World", ref worldInput, 64);
            ImGui.EndTable();
        }

        if (ImGui.BeginTable(
                "##ticket-options",
                3,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            UiHelpers.TextMuted("Tickets");
            ImGui.TableNextColumn();
            UiHelpers.TextMuted("VIP / Free");
            ImGui.TableNextColumn();
            UiHelpers.TextMuted("BOGO bonus");

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputInt("##tickets", ref ticketsInput, 1, 10);
            ticketsInput = Math.Max(1, ticketsInput);

            ImGui.TableNextColumn();
            if (ImGui.Checkbox("##vip-free-ticket", ref vipFreeTicket))
                persistence.SaveConfig();
            UiHelpers.TooltipOnHover("Adds entries without increasing the jackpot. Use for VIP comps, giveaways, or other free entries.");

            ImGui.TableNextColumn();
            ImGui.Checkbox("##bogo-bonus", ref addBogoBonusTickets);
            UiHelpers.TooltipOnHover("Adds the same number of bonus tickets as the entered amount. Example: entering 10 tickets with BOGO enabled adds 20 total tickets. The bonus tickets only add to jackpot if enabled in the settings window.");
            ImGui.EndTable();
        }

        DrawQuantityButtons();

        var (totalTicketsToAdd, jackpotTicketsToAdd) = GetTicketAddAmounts();
        if (ImGui.BeginTable(
                "##raffle-entry-actions",
                3,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("add", ImGuiTableColumnFlags.WidthStretch, 1.15f);
            ImGui.TableSetupColumn("target", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("undo", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Add Contestant",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                if (raffle.AddOrUpdate(nameInput, worldInput, totalTicketsToAdd, jackpotTicketsToAdd))
                {
                    nameInput = string.Empty;
                    worldInput = string.Empty;
                    ticketsInput = 1;
                }
            }

            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Add Target",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                if (raffle.AddCurrentTarget(totalTicketsToAdd, jackpotTicketsToAdd))
                    ticketsInput = 1;
            }
            UiHelpers.TooltipOnHover("Uses your current in-game target's name and home world. If that player already exists with the same name/world, their tickets are increased. VIP/free entries do not add to the jackpot.");

            ImGui.TableNextColumn();
            if (ImGui.Button(
                    "Undo",
                    new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(32f))))
            {
                raffle.Undo();
            }

            ImGui.EndTable();
        }

        UiHelpers.TextMutedWrapped(
            addBogoBonusTickets
                ? $"{totalTicketsToAdd:N0} chances, {jackpotTicketsToAdd:N0} paid. {raffle.LastStatus}"
                : raffle.LastStatus);
    }


    private (int TotalTickets, int JackpotTickets) GetTicketAddAmounts()
    {
        var enteredTickets = Math.Max(1, ticketsInput);
        var bonusTickets = addBogoBonusTickets ? enteredTickets : 0;
        var totalTickets = enteredTickets + bonusTickets;

        var jackpotTickets = vipFreeTicket ? 0 : enteredTickets;
        if (!vipFreeTicket && addBogoBonusTickets && Profile.BogoBonusTicketsCountTowardJackpot)
            jackpotTickets += bonusTickets;

        return (totalTickets, Math.Clamp(jackpotTickets, 0, totalTickets));
    }

    private void DrawQuantityButtons()
    {
        if (ImGui.BeginTable(
                "##ticket-presets",
                5,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Button("1", new Vector2(-1f, 0f))) ticketsInput = 1;
            ImGui.TableNextColumn();
            if (ImGui.Button("5", new Vector2(-1f, 0f))) ticketsInput = 5;
            ImGui.TableNextColumn();
            if (ImGui.Button("10", new Vector2(-1f, 0f))) ticketsInput = 10;
            ImGui.TableNextColumn();
            if (ImGui.Button("25", new Vector2(-1f, 0f))) ticketsInput = 25;
            ImGui.TableNextColumn();
            if (ImGui.Button("Reset", new Vector2(-1f, 0f))) ticketsInput = 1;
            ImGui.EndTable();
        }
        ticketsInput = Math.Max(1, ticketsInput);
    }

    private void DrawContestantsCard()
    {
        UiHelpers.Header("Contestants", $"{raffle.ParticipantCount:N0} contestant(s) · {raffle.TotalTickets:N0} ticket(s) · {raffle.TotalJackpotTickets:N0} paid");
        const int visibleContestants = 15;
        var entries = raffle.Entries.ToList();
        var tableRowHeight =
            ImGui.GetTextLineHeightWithSpacing() +
            ImGui.GetStyle().CellPadding.Y * 2f;
        var tableHeight =
            tableRowHeight * (visibleContestants + 1) +
            AirTablet.UI.TabletAppTheme.Px(4f);
        const float columnMargin = 0f;
        var ticketsWidth = MeasureColumnWidth(
            "Tickets",
            entries.Select(entry => entry.Tickets.ToString("N0")),
            columnMargin);
        var paidWidth = MeasureColumnWidth(
            "Paid",
            entries.Select(entry => entry.EffectiveJackpotTickets.ToString("N0")),
            columnMargin);
        var worldWidth = MeasureColumnWidth(
            "World",
            entries.Select(entry => string.IsNullOrWhiteSpace(entry.World) ? "—" : entry.World),
            columnMargin);
        var actionsWidth = MathF.Max(
                ImGui.CalcTextSize("Actions").X,
                ImGui.CalcTextSize("Delete").X +
                ImGui.GetStyle().FramePadding.X * 2f) +
            columnMargin;
        ImGui.PushStyleVar(
            ImGuiStyleVar.CellPadding,
            AirTablet.UI.TabletAppTheme.Px(new Vector2(5f, 7f)));
        if (ImGui.BeginTable(
                "##entries",
                5,
                ImGuiTableFlags.Borders
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Tickets", ImGuiTableColumnFlags.WidthFixed, ticketsWidth);
            ImGui.TableSetupColumn("Paid", ImGuiTableColumnFlags.WidthFixed, paidWidth);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, worldWidth);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionsWidth);
            ImGui.TableHeadersRow();

            foreach (var entry in entries)
            {
                ImGui.PushID(entry.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); UiHelpers.ClippedTextWithTooltip(entry.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Tickets.ToString("N0"));
                if (ImGui.IsItemHovered() && entry.HasFreeTickets)
                    UiHelpers.WrappedTooltip($"{entry.EffectiveJackpotTickets:N0} paid ticket(s), {entry.FreeTickets:N0} free/VIP ticket(s).");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.EffectiveJackpotTickets.ToString("N0"));
                if (ImGui.IsItemHovered() && entry.HasFreeTickets)
                    UiHelpers.WrappedTooltip($"{entry.FreeTickets:N0} free/VIP ticket(s) do not add to the jackpot.");
                ImGui.TableNextColumn(); UiHelpers.TextMuted(string.IsNullOrWhiteSpace(entry.World) ? "—" : entry.World);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("Delete")) pendingDelete = entry.Id;
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();

        if (ImGui.Button("Clear All")) AirTablet.UI.TabletAppTheme.OpenCenteredModal("Clear all contestants?");
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Clear all contestants?",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Remove every contestant from this raffle?");
            if (ImGui.Button("Clear", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0)))) { raffle.Clear(); AirTablet.UI.TabletAppTheme.CloseCenteredModal(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0)))) AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }
    }

    private static float MeasureColumnWidth(
        string header,
        IEnumerable<string> values,
        float padding)
    {
        var width = ImGui.CalcTextSize(header).X;
        foreach (var value in values)
            width = MathF.Max(width, ImGui.CalcTextSize(value).X);
        return width + padding;
    }

    private void DrawRandomizerCard()
    {
        ImGui.TextColored(RaffleTheme.Pink, "Randomizer");
        ImGui.Separator();
        DrawJackpotStrip();

        ImGui.Spacing();
        var buttonHeight = AirTablet.UI.TabletAppTheme.Px(36f);
        var spinnerHeight = Math.Clamp(
            ImGui.GetContentRegionAvail().Y
            - buttonHeight
            - ImGui.GetStyle().ItemSpacing.Y * 2f,
            AirTablet.UI.TabletAppTheme.Px(230f),
            AirTablet.UI.TabletAppTheme.Px(286f));
        var cardSize = new Vector2(
            0,
            spinnerHeight);
        if (UiHelpers.BeginCard(
                "##spinner",
                cardSize,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawSpinnerContents();
        }
        UiHelpers.EndCard();

        var buttonLabel = spinning ? "Picking..." : "Pick Random Winner";
        var buttonWidth = MathF.Min(
            AirTablet.UI.TabletAppTheme.Px(260f),
            ImGui.GetContentRegionAvail().X);
        var x = (ImGui.GetContentRegionAvail().X - buttonWidth) * 0.5f;
        if (x > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + x);
        ImGui.BeginDisabled(spinning);
        if (ImGui.Button(
                buttonLabel,
                new Vector2(buttonWidth, buttonHeight)))
        {
            if (raffle.TotalTickets <= 0)
                DalamudServices.ChatGui.Print("Add contestants before pulling a winner.", "RaffleManager");
            else
                pendingWinnerPick = true;
        }
        ImGui.EndDisabled();
    }

    private void DrawJackpotStrip()
    {
        if (UiHelpers.BeginCard(
                "##jackpotStrip",
                new Vector2(0, AirTablet.UI.TabletAppTheme.Px(140f)),
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (ImGui.BeginTable(
                    "##jackpotMetrics",
                    3,
                    ImGuiTableFlags.SizingStretchSame))
            {
                DrawMetricCell("Base", UiHelpers.Gil(Profile.BaseJackpot));
                DrawMetricCell("Ticket", UiHelpers.Gil(Profile.TicketPrice));
                DrawMetricCell("Paid", raffle.TotalJackpotTickets.ToString("N0"));
                DrawMetricCell("Tickets", raffle.TotalTickets.ToString("N0"));
                DrawMetricCell("Jackpot", UiHelpers.Gil(raffle.Jackpot));
                DrawMetricCell($"Winner {Profile.WinnerSplitPercent}%", UiHelpers.Gil(raffle.WinnerPayout));
                ImGui.EndTable();
            }
        }
        UiHelpers.EndCard();
    }

    private void DrawCompactSpinner()
    {
        var available = ImGui.GetContentRegionAvail();
        var displaySize = new Vector2(
            MathF.Max(AirTablet.UI.TabletAppTheme.Px(180f), available.X),
            MathF.Max(AirTablet.UI.TabletAppTheme.Px(54f), available.Y));
        var min = ImGui.GetCursorScreenPos();
        var max = min + displaySize;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(RaffleTheme.InputBg),
            AirTablet.UI.TabletAppTheme.Px(12f));
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(displayFlip ? RaffleTheme.Pink : RaffleTheme.Teal),
            AirTablet.UI.TabletAppTheme.Px(12f),
            ImDrawFlags.None,
            AirTablet.UI.TabletAppTheme.Px(2f));

        var textMin = min;
        var textWidth = displaySize.X;
        if (logo.Texture is { } customLogo)
        {
            var inset = AirTablet.UI.TabletAppTheme.Px(6f);
            var logoBoxSize = MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(36f),
                displaySize.Y - inset * 2f);
            var logoBoxMin = min + new Vector2(inset);
            var imageSize = FitImageSize(
                customLogo.Width,
                customLogo.Height,
                new Vector2(logoBoxSize));
            var imageMin = logoBoxMin + (new Vector2(logoBoxSize) - imageSize) * 0.5f;
            draw.AddImage(customLogo.Handle, imageMin, imageMin + imageSize);
            textMin.X += logoBoxSize + inset * 2f;
            textWidth -= logoBoxSize + inset * 2f;
        }

        var shownText = FitText(
            displayName,
            textWidth - AirTablet.UI.TabletAppTheme.Px(24f));
        var textSize = ImGui.CalcTextSize(shownText);
        draw.AddText(
            new Vector2(
                textMin.X + (textWidth - textSize.X) * 0.5f,
                min.Y + (displaySize.Y - textSize.Y) * 0.5f),
            ImGui.GetColorU32(displayFlip ? RaffleTheme.Pink : RaffleTheme.Teal),
            shownText);
        ImGui.Dummy(displaySize);
    }

    private static void DrawMetric(string label, string value)
    {
        UiHelpers.TextMutedWrapped(label);
        ImGui.PushStyleColor(ImGuiCol.Text, RaffleTheme.Teal);
        ImGui.TextWrapped(value);
        ImGui.PopStyleColor();
    }

    private static void DrawMetricCell(string label, string value)
    {
        ImGui.TableNextColumn();
        DrawMetric(label, value);
        ImGui.Spacing();
    }

    private void DrawSpinnerContents()
    {
        var size = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rowWidth = size.X;
        var customLogo = logo.Texture;
        var hasLogo = customLogo is not null;
        var drawBothLogos =
            hasLogo
            && rowWidth >= AirTablet.UI.TabletAppTheme.Px(760f);
        var drawOneLogo =
            hasLogo
            && !drawBothLogos
            && rowWidth >= AirTablet.UI.TabletAppTheme.Px(620f);

        // Hide logos entirely when no custom logo is set or at the smallest sizes so the picker always remains visible and centered.
        var logoSize = Math.Clamp(
            size.Y * 0.72f,
            AirTablet.UI.TabletAppTheme.Px(132f),
            AirTablet.UI.TabletAppTheme.Px(184f));
        var logoCount = drawBothLogos ? 2f : drawOneLogo ? 1f : 0f;
        var displayHeight = Math.Clamp(
            (logoCount > 0f ? logoSize : size.Y * 0.42f) * 0.66f,
            AirTablet.UI.TabletAppTheme.Px(92f),
            AirTablet.UI.TabletAppTheme.Px(126f));
        var maxDisplayWidth = drawBothLogos
            ? AirTablet.UI.TabletAppTheme.Px(380f)
            : drawOneLogo
                ? AirTablet.UI.TabletAppTheme.Px(500f)
                : MathF.Max(
                    AirTablet.UI.TabletAppTheme.Px(320f),
                    rowWidth - AirTablet.UI.TabletAppTheme.Px(32f));
        var minDisplayWidth = MathF.Min(
            rowWidth - AirTablet.UI.TabletAppTheme.Px(24f),
            AirTablet.UI.TabletAppTheme.Px(logoCount > 0f ? 260f : 300f));
        var availableDisplayWidth = rowWidth - (logoSize * logoCount) - (spacing * MathF.Max(0f, logoCount));
        var displayWidth = Math.Clamp(availableDisplayWidth, minDisplayWidth, maxDisplayWidth);
        var totalWidth = (logoSize * logoCount) + displayWidth + (spacing * MathF.Max(0f, logoCount));
        var rowHeight = MathF.Max(logoCount > 0f ? logoSize : displayHeight, displayHeight);
        var y = ImGui.GetCursorPosY() + ((size.Y - rowHeight) * 0.5f);
        ImGui.SetCursorPosY(y);

        var offsetX = (rowWidth - totalWidth) * 0.5f;
        if (offsetX > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        if (logoCount > 0f)
        {
            DrawLogoImage(customLogo!, new Vector2(logoSize, logoSize));
            ImGui.SameLine();
        }

        var displayYOffset = logoCount > 0f ? (logoSize - displayHeight) * 0.5f : 0f;
        if (displayYOffset > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + displayYOffset);

        var displayMin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var displayMax = displayMin + new Vector2(displayWidth, displayHeight);
        draw.AddRectFilled(
            displayMin,
            displayMax,
            ImGui.GetColorU32(RaffleTheme.InputBg),
            AirTablet.UI.TabletAppTheme.Px(12f));
        draw.AddRect(
            displayMin,
            displayMax,
            ImGui.GetColorU32(RaffleTheme.Border),
            AirTablet.UI.TabletAppTheme.Px(12f),
            0,
            AirTablet.UI.TabletAppTheme.Px(2f));
        var textColor = ImGui.GetColorU32(displayFlip ? RaffleTheme.Pink : RaffleTheme.Teal);
        var shownText = FitText(
            displayName,
            displayWidth - AirTablet.UI.TabletAppTheme.Px(24f));
        var textSize = ImGui.CalcTextSize(shownText);
        draw.AddText(displayMin + new Vector2((displayWidth - textSize.X) * 0.5f, (displayHeight - textSize.Y) * 0.5f), textColor, shownText);
        ImGui.Dummy(new Vector2(displayWidth, displayHeight));

        if (displayYOffset > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - displayYOffset);

        if (drawBothLogos)
        {
            ImGui.SameLine();
            DrawLogoImage(customLogo!, new Vector2(logoSize, logoSize));
        }
    }

    private static string FitText(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        const string suffix = "...";
        for (var len = text.Length - 1; len > 1; len--)
        {
            var candidate = text[..len] + suffix;
            if (ImGui.CalcTextSize(candidate).X <= maxWidth) return candidate;
        }
        return suffix;
    }

    private static void DrawLogoImage(Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap texture, Vector2 boxSize)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + boxSize;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(RaffleTheme.InputBg),
            AirTablet.UI.TabletAppTheme.Px(12f));
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(RaffleTheme.Border),
            AirTablet.UI.TabletAppTheme.Px(12f),
            0,
            AirTablet.UI.TabletAppTheme.Px(2f));

        var imageSize = FitImageSize(texture.Width, texture.Height, boxSize - AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f)));
        var imagePos = min + ((boxSize - imageSize) * 0.5f);
        draw.AddImage(texture.Handle, imagePos, imagePos + imageSize);
        ImGui.Dummy(boxSize);
    }

    private static Vector2 FitImageSize(int textureWidth, int textureHeight, Vector2 maxSize)
    {
        if (textureWidth <= 0 || textureHeight <= 0)
            return maxSize;

        var source = new Vector2(textureWidth, textureHeight);
        var scale = MathF.Min(maxSize.X / source.X, maxSize.Y / source.Y);
        // Allow a little upscaling for small logo files, but cap it so they do not become overly blurry/pixelated.
        scale = MathF.Min(scale, 1.50f);
        return new Vector2(MathF.Max(1f, source.X * scale), MathF.Max(1f, source.Y * scale));
    }

    private void StartSpin()
    {
        if (raffle.TotalTickets <= 0)
        {
            DalamudServices.ChatGui.Print("Add contestants before pulling a winner.", "RaffleManager");
            return;
        }
        spinning = true;
        displayName = "...";
        nextTickSeconds = 0;
        sound.Prepare();
        sound.PlayTick();
        spinWatch.Restart();
    }

    private void UpdateSpinAnimation()
    {
        if (!spinning) return;
        var elapsed = spinWatch.Elapsed.TotalSeconds;
        if (elapsed >= 8.0)
        {
            spinning = false;
            spinWatch.Stop();
            var winner = raffle.PullWinner();
            if (winner is not null)
            {
                displayName = winner.DisplayName;
                winnerPopup = winner;
                announcements.AnnounceWinner(winner);
            }
            return;
        }

        if (elapsed < nextTickSeconds) return;
        var entry = raffle.PickRandomTicketOwner();
        if (entry is not null)
        {
            displayName = entry.DisplayName;
            displayFlip = !displayFlip;
            sound.PlayTick();
        }

        var delay = elapsed switch
        {
            < 4.0 => 0.05,
            < 7.0 => 0.10,
            < 9.0 => 0.20,
            _ => 0.50,
        };
        nextTickSeconds = elapsed + delay;
    }

    private void DrawWinnerPopup()
    {
        if (winnerPopup is null) return;

        AirTablet.UI.TabletAppTheme.OpenCenteredModal("Winner Announcement###WinnerPopup");
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Winner Announcement###WinnerPopup",
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawWinnerPopupContents();
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }
    }

    private void DrawWinnerPopupContents()
    {
        if (winnerPopup is null) return;

        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var contentStartX = ImGui.GetCursorPosX();
        var content = ImGui.GetContentRegionAvail();
        var max = min + content;
        var rounding = AirTablet.UI.TabletAppTheme.Px(14f);
        var popupBackground = AirTablet.UI.TabletAppTheme.IsActive
            ? AirTablet.UI.TabletAppTheme.Surface
            : new Vector4(0.08f, 0.03f, 0.14f, 1f);

        draw.AddRectFilled(min, max, ImGui.GetColorU32(popupBackground), rounding);
        draw.AddRect(
            min,
            max,
            ImGui.GetColorU32(RaffleTheme.Pink),
            rounding,
            0,
            AirTablet.UI.TabletAppTheme.Px(3f));
        draw.AddRect(
            min + AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f)),
            max - AirTablet.UI.TabletAppTheme.Px(new Vector2(8f, 8f)),
            ImGui.GetColorU32(RaffleTheme.Border),
            AirTablet.UI.TabletAppTheme.Px(12f),
            0,
            AirTablet.UI.TabletAppTheme.Px(1.5f));

        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + AirTablet.UI.TabletAppTheme.Px(12f));
        CenteredText(Profile.VenueName, RaffleTheme.Muted);
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + AirTablet.UI.TabletAppTheme.Px(4f));
        CenteredText("WE HAVE A WINNER", RaffleTheme.Pink);

        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + AirTablet.UI.TabletAppTheme.Px(10f));
        var rowStartY = ImGui.GetCursorPosY();
        var customLogo = logo.Texture;
        var hasLogo = customLogo is not null;
        var showTwoLogos =
            hasLogo
            && content.X >= AirTablet.UI.TabletAppTheme.Px(760f);
        var showOneLogo =
            hasLogo
            && !showTwoLogos
            && content.X >= AirTablet.UI.TabletAppTheme.Px(540f);
        var logoCount = showTwoLogos ? 2 : showOneLogo ? 1 : 0;
        var rowHeight = Math.Clamp(
            content.X * 0.20f,
            AirTablet.UI.TabletAppTheme.Px(126f),
            AirTablet.UI.TabletAppTheme.Px(178f));
        var logoBox = logoCount > 0 ? rowHeight : 0f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var maximumTextWidth =
            content.X
            - logoBox * logoCount
            - spacing * logoCount
            - AirTablet.UI.TabletAppTheme.Px(12f);
        var winnerTextWidth = Math.Clamp(
            maximumTextWidth,
            AirTablet.UI.TabletAppTheme.Px(230f),
            AirTablet.UI.TabletAppTheme.Px(460f));
        var rowWidth =
            winnerTextWidth
            + logoBox * logoCount
            + spacing * logoCount;
        var rowX = (content.X - rowWidth) * 0.5f;
        if (rowX > 0) ImGui.SetCursorPosX(contentStartX + rowX);

        if (logoCount > 0)
        {
            DrawLogoImage(customLogo!, new Vector2(logoBox, logoBox));
            ImGui.SameLine();
        }

        var textBoxMin = ImGui.GetCursorScreenPos();
        var textBoxSize = new Vector2(winnerTextWidth, rowHeight);
        draw.AddRectFilled(
            textBoxMin,
            textBoxMin + textBoxSize,
            ImGui.GetColorU32(RaffleTheme.InputBg),
            AirTablet.UI.TabletAppTheme.Px(12f));
        draw.AddRect(
            textBoxMin,
            textBoxMin + textBoxSize,
            ImGui.GetColorU32(RaffleTheme.Teal),
            AirTablet.UI.TabletAppTheme.Px(12f),
            0,
            AirTablet.UI.TabletAppTheme.Px(2f));

        var winnerName = FitText(
            winnerPopup.DisplayName,
            winnerTextWidth - AirTablet.UI.TabletAppTheme.Px(30f));
        var nameSize = ImGui.CalcTextSize(winnerName);
        draw.AddText(textBoxMin + new Vector2((winnerTextWidth - nameSize.X) * 0.5f, (textBoxSize.Y - nameSize.Y) * 0.40f), ImGui.GetColorU32(RaffleTheme.Teal), winnerName);

        var ticketText = $"{winnerPopup.Tickets:N0} of {winnerPopup.TotalTickets:N0} tickets";
        if (winnerPopup.JackpotTickets < winnerPopup.Tickets)
            ticketText += $" · {winnerPopup.JackpotTickets:N0} paid";
        var ticketSize = ImGui.CalcTextSize(ticketText);
        draw.AddText(textBoxMin + new Vector2((winnerTextWidth - ticketSize.X) * 0.5f, (textBoxSize.Y - ticketSize.Y) * 0.64f), ImGui.GetColorU32(RaffleTheme.Muted), ticketText);
        ImGui.Dummy(textBoxSize);

        if (showTwoLogos)
        {
            ImGui.SameLine();
            DrawLogoImage(customLogo!, new Vector2(logoBox, logoBox));
        }

        ImGui.SetCursorPosY(
            rowStartY +
            rowHeight +
            AirTablet.UI.TabletAppTheme.Px(14f));
        var payoutLine = $"Winner Payout: {UiHelpers.Gil(winnerPopup.Payout)}";
        CenteredText(payoutLine, RaffleTheme.Teal);
        CenteredText($"Total Jackpot: {UiHelpers.Gil(winnerPopup.Jackpot)}  ·  Split: {winnerPopup.SplitPercent}%", RaffleTheme.Muted);
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + AirTablet.UI.TabletAppTheme.Px(8f));

        var closeWidth = AirTablet.UI.TabletAppTheme.Px(150f);
        var closeHeight = AirTablet.UI.TabletAppTheme.Px(34f);
        ImGui.SetCursorPosX(
            contentStartX +
            MathF.Max(0f, (content.X - closeWidth) * 0.5f));
        if (ImGui.Button("Close", new Vector2(closeWidth, closeHeight)))
        {
            winnerPopup = null;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.Dummy(
            AirTablet.UI.TabletAppTheme.Px(new Vector2(0f, 6f)));
    }

    private static void CenteredText(string text, Vector4 color)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(text);
        var offset = (width - textSize.X) * 0.5f;
        if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextColored(color, text);
    }

    private void DrawClearHistoryConfirmation()
    {
        if (!pendingClearWinnerHistory) return;
        AirTablet.UI.TabletAppTheme.OpenCenteredModal("Delete winner history?");
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Delete winner history?",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34f);
            ImGui.TextWrapped($"Delete all saved winner history for the '{Profile.Name}' venue profile? Current contestants will not be removed.");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();

            if (ImGui.Button("Delete History", AirTablet.UI.TabletAppTheme.Px(new Vector2(140, 0))))
            {
                raffle.ClearWinnerHistory();
                pendingClearWinnerHistory = false;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
            {
                pendingClearWinnerHistory = false;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }
    }

    private void DrawPickWinnerConfirmation()
    {
        if (!pendingWinnerPick) return;
        AirTablet.UI.TabletAppTheme.OpenCenteredModal("Pick random winner?");
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Pick random winner?",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34f);
            ImGui.TextWrapped($"Pick a random winner from the {raffle.TotalTickets:N0} ticket(s) in the '{Profile.Name}' venue profile?");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();

            if (ImGui.Button("Pick Winner", AirTablet.UI.TabletAppTheme.Px(new Vector2(140, 0))))
            {
                pendingWinnerPick = false;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
                StartSpin();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
            {
                pendingWinnerPick = false;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }
    }

    private void DrawDeleteConfirmation()
    {
        if (pendingDelete is null) return;
        AirTablet.UI.TabletAppTheme.OpenCenteredModal("Delete contestant?");
        if (AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Delete contestant?",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Remove this contestant from the raffle?");
            if (ImGui.Button("Delete", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
            {
                raffle.Remove(pendingDelete.Value);
                pendingDelete = null;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", AirTablet.UI.TabletAppTheme.Px(new Vector2(120, 0))))
            {
                pendingDelete = null;
                AirTablet.UI.TabletAppTheme.CloseCenteredModal();
            }
            AirTablet.UI.TabletAppTheme.EndCenteredModal();
        }
    }
}
