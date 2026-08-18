using System.Numerics;
using System.Text.RegularExpressions;
using BarManager.Models;
using BarManager.Services;
using BarManager.UI.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace BarManager.UI.Tabs;

internal sealed class GambaDrinkTab : IDisposable
{
    private static readonly Regex PartyRandomRegex = new(@"^(?:(?<name>.+?)[@＠](?<world>[^:]+):?\s*)?Random!\s*(?:\((?<rangeMin>\d+)\s*-\s*(?<rangeMax>\d+)\)\s*)?(?<roll>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CombinedRandomRegex = new(@"^(?<name>.+?)[@＠](?<world>[^:]+):?\s*Random!\s*(?:\((?<rangeMin>\d+)\s*-\s*(?<rangeMax>\d+)\)\s*)?(?<roll>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParenthesizedPartyRandomRegex = new(@"^\((?<name>[^)]+)\)\s*Random!\s*(?:\((?<rangeMin>\d+)\s*-\s*(?<rangeMax>\d+)\)\s*)?(?<roll>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NamedRandomRegex = new(@"^(?<name>[^:]+):\s*Random!\s*(?:\((?<rangeMin>\d+)\s*-\s*(?<rangeMax>\d+)\)\s*)?(?<roll>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RandomRollRegex = new(@"Random!\s*(?:\((?<rangeMin>\d+)\s*-\s*(?<rangeMax>\d+)\)\s*)?(?<roll>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingParenthesizedNameRegex = new(@"^\((?<name>[^)]+)\)", RegexOptions.Compiled);
    private static readonly object AnnouncementLock = new();
    private static string lastGlobalAnnouncement = string.Empty;
    private static DateTime lastGlobalAnnouncementAt = DateTime.MinValue;
    private static readonly HashSet<string> PendingGlobalAnnouncements = new(StringComparer.Ordinal);

    private readonly record struct ParsedPartyRoll(string Name, string World, int Roll, int? RangeMin, int? RangeMax);

    private static readonly string[] KnownWorlds =
    {
        "Adamantoise", "Aegis", "Alexander", "Alpha", "Anima", "Asura", "Atomos", "Bahamut", "Balmung", "Behemoth", "Belias", "Brynhildr",
        "Cactuar", "Carbuncle", "Cerberus", "Chocobo", "Coeurl", "Diabolos", "Durandal", "Excalibur", "Exodus", "Faerie", "Famfrit",
        "Fenrir", "Garuda", "Gilgamesh", "Goblin", "Gungnir", "Hades", "Halicarnassus", "Hyperion", "Ifrit", "Ixion", "Jenova",
        "Kujata", "Lamia", "Leviathan", "Louisoix", "Maduin", "Malboro", "Mandragora", "Marilith", "Masamune", "Mateus", "Midgardsormr",
        "Moogle", "Odin", "Omega", "Pandaemonium", "Phantom", "Phoenix", "Ragnarok", "Raiden", "Ravana", "Ridill", "Sagittarius",
        "Sargatanas", "Sephirot", "Seraph", "Shinryu", "Shiva", "Siren", "Sophia", "Spriggan", "Tiamat", "Titan", "Tonberry",
        "Twintania", "Typhon", "Ultima", "Ultros", "Unicorn", "Valefor", "Yojimbo", "Zalera", "Zeromus", "Zodiark"
    };

    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly ChatCommandService chatCommands = new();
    private readonly Queue<string> pendingPartyAnnouncements = new();
    private readonly Queue<string> pendingChatCommands = new();
    private DateTime nextPartyAnnouncementAt = DateTime.MinValue;
    private GambaSessionRecord? current;
    private string customerName = string.Empty;
    private string customerWorld = string.Empty;
    private int drinks = 1;
    private int rollInput;
    private string pasteRolls = string.Empty;
    private int rollsRemaining;
    private string status = "Live party-chat roll tracking is ready.";
    private string lastPartyRollFingerprint = string.Empty;
    private DateTime lastPartyRollAt = DateTime.MinValue;
    private int lastAnnouncedRollsRemaining = int.MinValue;
    private bool awaitingBartenderBonusRoll;
    private DateTime awaitingBartenderBonusRollSince = DateTime.MinValue;
    private int queuedBartenderBonusRolls;
    private int pendingBartenderBonusResults;
    private DateTime nextBartenderBonusCommandAt = DateTime.MinValue;
    private DateTime lastAutomaticBartenderBonusCommandAt = DateTime.MinValue;
    private bool autoEndAfterBartenderBonusRoll;
    private bool resolvingCustomerRollWasLocalPlayer;
    private GambaSessionRecord? retainedRollHistory;
    private bool pendingCancelSession;

    public GambaDrinkTab(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
        retainedRollHistory = config.CurrentAudit.GambaSessions.LastOrDefault();
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
    }

    public bool HasActiveSession => current is not null && current.EndedAt is null;

    public void Draw()
    {
        var venue = config.ActiveVenue;
        var audit = config.CurrentAudit;
        var gamba = venue.Gamba;
        SyncRollsRemainingFromSession();

        if (ImGui.BeginChild("##GambaScroll", AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 0)), false))
        {
            UiHelpers.TextWrappedMuted(
                $"Track paid {gamba.DrinkName} rolls from party chat for the selected customer. " +
                $"This venue expects /dice {gamba.MaxRoll}.");
            ImGui.Spacing();

            if (retainedRollHistory is not null &&
                !config.CurrentAudit.GambaSessions.Contains(retainedRollHistory))
            {
                retainedRollHistory = config.CurrentAudit.GambaSessions.LastOrDefault();
            }

            var workspaceAvail = ImGui.GetContentRegionAvail();
            var estimatedLeftWidth = MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(300f),
                workspaceAvail.X * 0.48f);
            var sessionCardHeight = CalculateSessionCardHeight(gamba, estimatedLeftWidth);
            var manualHeight = CalculateManualCardHeight(
                gamba.AllowPasteImport,
                status,
                estimatedLeftWidth);
            var historySession = current ?? retainedRollHistory;
            var leftStackHeight =
                sessionCardHeight +
                ImGui.GetStyle().ItemSpacing.Y +
                manualHeight;
            var historyCardHeight = CalculateHistoryCardHeight(
                historySession?.Rolls.Count ?? 0,
                leftStackHeight);
            var workspaceHeight = MathF.Max(
                historyCardHeight,
                leftStackHeight);
            if (!ImGui.BeginTable(
                    "##GambaWorkspace",
                    2,
                    ImGuiTableFlags.SizingStretchProp,
                    new Vector2(0, workspaceHeight)))
            {
                ImGui.EndChild();
                return;
            }
            ImGui.TableSetupColumn("session", ImGuiTableColumnFlags.WidthStretch, 0.48f);
            ImGui.TableSetupColumn("history", ImGuiTableColumnFlags.WidthStretch, 0.52f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (UiHelpers.BeginCard(
                    "##GambaSessionCard",
                    new Vector2(0, sessionCardHeight),
                    ImGuiWindowFlags.NoScrollbar))
            {
                UiHelpers.SectionTitle(current is null ? "New Session" : "Live Session");
                if (current is null)
                {
                    if (ImGui.BeginTable(
                            "##new-session-identity",
                            2,
                            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
                    {
                        ImGui.TableSetupColumn("customer", ImGuiTableColumnFlags.WidthStretch, 1.25f);
                        ImGui.TableSetupColumn("world", ImGuiTableColumnFlags.WidthStretch, 0.85f);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputTextWithHint("##gamba-customer", "Customer", ref customerName, 128);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputTextWithHint("##gamba-world", "World", ref customerWorld, 64);
                        ImGui.EndTable();
                    }

                    if (ImGui.BeginTable(
                            "##new-session-purchase",
                            3,
                            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
                    {
                        ImGui.TableSetupColumn("drinks", ImGuiTableColumnFlags.WidthStretch, 0.75f);
                        ImGui.TableSetupColumn("rolls", ImGuiTableColumnFlags.WidthStretch, 1.1f);
                        ImGui.TableSetupColumn("target", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(92f));
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputInt($"##{gamba.DrinkName}s", ref drinks);
                        drinks = Math.Clamp(drinks, 1, 500);

                        ImGui.TableNextColumn();
                        UiHelpers.TextWrappedMuted($"{drinks * gamba.RollsPerDrink:N0} roll(s)");

                        ImGui.TableNextColumn();
                        if (ImGui.Button("Use target", new Vector2(-1f, 0f)))
                            UseCurrentTarget();
                        ImGui.EndTable();
                    }

                    var canStartSession = !string.IsNullOrWhiteSpace(customerName);
                    ImGui.BeginDisabled(!canStartSession);
                    if (ImGui.Button(
                            "Start Session",
                            new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(30f))))
                    {
                        StartSession(gamba);
                    }
                    ImGui.EndDisabled();
                    if (!canStartSession)
                        UiHelpers.TooltipOnHover("Enter a customer name or use the current player target before starting a session.");
                }
                else
                {
                    if (ImGui.BeginTable("##liveSessionSummary", 2, ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthStretch);

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(BarManagerTheme.Muted, "Player");
                        ImGui.TextColored(BarManagerTheme.Gold, current.CustomerDisplay);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(BarManagerTheme.Muted, "Rolls remaining");
                        ImGui.Text($"{rollsRemaining:N0} / {current.RollsAllowed:N0}");

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(BarManagerTheme.Muted, "Session payout");
                        ImGui.TextColored(BarManagerTheme.Green, UiHelpers.Gil(current.TotalPayout));
                        ImGui.TableNextColumn();
                        ImGui.TextColored(BarManagerTheme.Muted, "Jackpot");
                        ImGui.TextColored(BarManagerTheme.Gold, UiHelpers.Gil(audit.JackpotCurrent));

                        ImGui.EndTable();
                    }

                    DrawActiveBonusStatus(gamba);
                    if (ImGui.BeginTable(
                            "##live-session-actions",
                            2,
                            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.BeginDisabled(rollsRemaining > 0);
                        if (ImGui.Button("End & Save", new Vector2(-1f, 0f)))
                            EndSession();
                        ImGui.EndDisabled();
                        if (rollsRemaining > 0)
                            UiHelpers.TooltipOnHover($"Resolve the remaining {rollsRemaining:N0} roll(s) before ending and saving this session.");

                        ImGui.TableNextColumn();
                        if (ImGui.Button("Cancel Session", new Vector2(-1f, 0f)))
                            pendingCancelSession = true;

                        ImGui.EndTable();
                    }
                }
            }
            UiHelpers.EndCard();

            if (UiHelpers.BeginCard(
                    "##ManualRollCard",
                    new Vector2(0, manualHeight),
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                UiHelpers.SectionTitle("Manual / Paste Entry");
                ImGui.BeginDisabled(current is null);
                if (ImGui.BeginTable(
                        "##manual-roll-entry",
                        2,
                        ImGuiTableFlags.SizingStretchProp
                        | ImGuiTableFlags.NoSavedSettings))
                {
                    ImGui.TableSetupColumn(
                        "roll",
                        ImGuiTableColumnFlags.WidthStretch,
                        1f);
                    ImGui.TableSetupColumn(
                        "resolve",
                        ImGuiTableColumnFlags.WidthFixed,
                        AirTablet.UI.TabletAppTheme.Px(112f));
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1f);
                    ImGui.InputInt("##manual-roll", ref rollInput);
                    ImGui.TableNextColumn();
                    if (ImGui.Button("Resolve Roll", new Vector2(-1f, 0f)))
                        ResolveRoll(rollInput, "manual");
                    ImGui.EndTable();
                }
                if (gamba.AllowPasteImport)
                {
                    UiHelpers.TextMuted("Paste one or more roll results");
                    ImGui.InputTextMultiline(
                        "##paste-rolls",
                        ref pasteRolls,
                        4096,
                        AirTablet.UI.TabletAppTheme.Px(new Vector2(-1, 58)));
                    if (ImGui.Button(
                            "Import Paste",
                            new Vector2(-1f, AirTablet.UI.TabletAppTheme.Px(30f))))
                    {
                        foreach (Match m in Regex.Matches(pasteRolls, @"\d+"))
                        {
                            if (int.TryParse(m.Value, out var roll) && current is not null && CurrentRollsRemaining() > 0)
                                ResolveRoll(roll, "paste");
                        }
                        pasteRolls = string.Empty;
                    }
                }
                ImGui.EndDisabled();
                UiHelpers.TextWrappedMuted(status);
            }
            UiHelpers.EndCard();

            ImGui.TableNextColumn();

            if (UiHelpers.BeginCard(
                    "##GambaHistoryCard",
                    new Vector2(0, historyCardHeight),
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                UiHelpers.SectionTitle("Roll History");
                if (historySession is null || historySession.Rolls.Count == 0)
                {
                    UiHelpers.TextWrappedMuted("Roll results appear here during a session and remain visible until the next session starts.");
                }
                else if (ImGui.BeginTable(
                             "##liveRolls",
                             5,
                             ImGuiTableFlags.Borders
                             | ImGuiTableFlags.RowBg
                             | ImGuiTableFlags.Resizable))
                {
                    ImGui.TableSetupColumn("#");
                    ImGui.TableSetupColumn("Roll");
                    ImGui.TableSetupColumn("Tier");
                    ImGui.TableSetupColumn("Payout");
                    ImGui.TableSetupColumn("Jackpot +");
                    ImGui.TableHeadersRow();
                    foreach (var (record, index) in historySession.Rolls.Select((r, i) => (r, i + 1)))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn(); ImGui.Text(index.ToString());
                        ImGui.TableNextColumn(); ImGui.Text(record.Roll.ToString());
                        ImGui.TableNextColumn(); ImGui.TextColored(GetTierColor(record.Tier), record.Tier);
                        ImGui.TableNextColumn();
                        var payoutText = UiHelpers.Gil(record.Payout);
                        if (!string.IsNullOrWhiteSpace(record.BonusName) && record.BonusMultiplier > 1f)
                            payoutText += $" ({record.BonusName} x{record.BonusMultiplier:0.##})";
                        ImGui.TextColored(BarManagerTheme.Green, payoutText);
                        ImGui.TableNextColumn(); ImGui.TextColored(BarManagerTheme.Gold, UiHelpers.Gil(record.JackpotContribution));
                    }
                    ImGui.EndTable();
                }
            }
            UiHelpers.EndCard();
            ImGui.EndTable();
        }
        ImGui.EndChild();
        DrawCancelSessionConfirmation();
    }

    private void StartSession(GambaSettings gamba)
    {
        if (string.IsNullOrWhiteSpace(customerName))
        {
            status = "Enter a customer name before starting a session.";
            return;
        }

        retainedRollHistory = null;
        current = new GambaSessionRecord
        {
            CustomerName = customerName.Trim(),
            CustomerWorld = customerWorld.Trim(),
            DrinksPurchased = drinks,
            RollsAllowed = drinks * gamba.RollsPerDrink,
        };
        rollsRemaining = current.RollsAllowed;
        lastPartyRollFingerprint = string.Empty;
        lastPartyRollAt = DateTime.MinValue;
        lastAnnouncedRollsRemaining = int.MinValue;
        awaitingBartenderBonusRoll = false;
        queuedBartenderBonusRolls = 0;
        pendingBartenderBonusResults = 0;
        nextBartenderBonusCommandAt = DateTime.MinValue;
        lastAutomaticBartenderBonusCommandAt = DateTime.MinValue;
        autoEndAfterBartenderBonusRoll = false;
        status = $"Tracking party-chat /dice {gamba.MaxRoll} rolls for {current.CustomerDisplay}. Plain /dice only counts when max roll is 999. Bartender bonus checks start after the customer rolls.";
    }

    private void ResolveRoll(int roll, string source)
    {
        if (current is null) return;
        SyncRollsRemainingFromSession();
        var venue = config.ActiveVenue;
        var gamba = venue.Gamba;
        if (roll < gamba.MinRoll || roll > gamba.MaxRoll)
        {
            status = $"Ignored {source} roll {roll}; expected {gamba.MinRoll}-{gamba.MaxRoll}.";
            return;
        }
        if (rollsRemaining <= 0)
        {
            status = "Ignored roll; the active session has no paid rolls remaining.";
            return;
        }

        var jackpotBefore = config.CurrentAudit.JackpotCurrent;
        var result = GambaEngine.Resolve(roll, jackpotBefore, gamba);
        var basePayout = result.Payout;
        var payout = basePayout;
        var appliedBonusName = string.Empty;
        var appliedMultiplier = 1f;
        var isWin = result.JackpotWin || basePayout > 0;

        if (isWin)
        {
            var appliedBonuses = new List<string>();
            var rangeBonus = GambaEngine.FindRollRangeMultiplier(roll, gamba);
            if (rangeBonus is not null && (!result.JackpotWin || rangeBonus.AppliesToJackpot))
            {
                var rangeMultiplier = MathF.Max(1f, rangeBonus.Multiplier);
                if (rangeMultiplier > 1f)
                {
                    appliedMultiplier *= rangeMultiplier;
                    appliedBonuses.Add($"Roll range {rangeBonus.MinimumRoll:N0}-{rangeBonus.MaximumRoll:N0}");
                }
            }

            if (current.LossStreakBonusActive)
            {
                if (!result.JackpotWin || gamba.LossStreakBonusAppliesToJackpot)
                {
                    var multiplier = MathF.Max(1f, gamba.LossStreakBonusMultiplier);
                    if (multiplier > 1f)
                    {
                        appliedMultiplier *= multiplier;
                        appliedBonuses.Add(string.IsNullOrWhiteSpace(gamba.LossStreakBonusName) ? "Loss Streak Bonus" : gamba.LossStreakBonusName.Trim());
                    }
                }
                current.LossStreakBonusActive = false;
                current.LossStreakBonusTurnsRemaining = 0;
                current.ConsecutiveLosses = 0;
            }
            else if (current.BartenderRollBonusActive)
            {
                if (!result.JackpotWin || gamba.BartenderRollBonusAppliesToJackpot)
                {
                    var multiplier = MathF.Max(1f, gamba.BartenderRollBonusMultiplier);
                    if (multiplier > 1f)
                    {
                        appliedMultiplier *= multiplier;
                        appliedBonuses.Add(string.IsNullOrWhiteSpace(gamba.BartenderRollBonusName) ? "Bartender Bonus" : gamba.BartenderRollBonusName.Trim());
                    }
                }
                current.BartenderRollBonusActive = false;
                current.BartenderRollBonusTurnsRemaining = 0;
            }

            if (appliedMultiplier > 1f)
            {
                payout = ApplyBonusMultiplier(basePayout, appliedMultiplier);
                appliedBonusName = string.Join(" + ", appliedBonuses);
            }
        }

        var contribution = CalculateJackpotContribution(venue, gamba);

        current.Rolls.Add(new GambaRollRecord
        {
            Roll = roll,
            Tier = result.Tier,
            Payout = payout,
            JackpotWin = result.JackpotWin,
            FreeRoll = result.FreeRoll,
            JackpotContribution = contribution,
            BonusName = appliedBonusName,
            BonusMultiplier = appliedMultiplier,
            BasePayout = basePayout,
        });

        config.CurrentAudit.PrizesPaidOut += payout;
        if (result.JackpotWin)
            config.CurrentAudit.JackpotCurrent = venue.JackpotBase;
        else if (contribution > 0)
            config.CurrentAudit.JackpotCurrent += contribution;

        SyncRollsRemainingFromSession();

        UpdateBonusStateAfterRoll(gamba, isWin);

        rollInput = 0;
        var bonusText = string.IsNullOrWhiteSpace(appliedBonusName) ? string.Empty : $" with {appliedBonusName} x{appliedMultiplier:0.##}";
        status = $"Resolved {roll} from {source}: {result.Tier}, {UiHelpers.Gil(payout)}{bonusText}. Rolls left: {rollsRemaining:N0}.";
        persistence.SaveNow();

        if (result.JackpotWin)
        {
            HandleJackpotWin(gamba, jackpotBefore, payout);
            return;
        }

        AnnounceRollsLeftIfNeeded(gamba);

        var shouldAutoEnd = gamba.AutoEndWhenRollsUsed && rollsRemaining <= 0;
        QueueAutomaticBartenderBonusRoll(gamba);

        if (shouldAutoEnd)
        {
            if (queuedBartenderBonusRolls > 0 || pendingBartenderBonusResults > 0 || awaitingBartenderBonusRoll)
                autoEndAfterBartenderBonusRoll = true;
            else
                EndSession();
        }
    }


    private int CurrentRollsRemaining()
    {
        if (current is null)
            return 0;

        var baseAllowed = Math.Max(0, current.RollsAllowed);
        var bonusRolls = current.Rolls.Count(r => r.FreeRoll);
        var resolvedRolls = current.Rolls.Count;
        return Math.Max(0, baseAllowed + bonusRolls - resolvedRolls);
    }

    private void SyncRollsRemainingFromSession()
    {
        if (current is null)
            return;

        // Keep the UI/auto-end counter derived from session history instead of
        // relying only on a mutable field. This prevents a desync where the live
        // counter can reach 0 even though the saved session still shows fewer
        // resolved rolls than were originally allowed. Free-roll awards are
        // included as extra available rolls.
        rollsRemaining = CurrentRollsRemaining();
    }

    private static int ApplyBonusMultiplier(int payout, float multiplier)
    {
        if (payout <= 0)
            return payout;
        var multiplied = Math.Round(payout * (double)MathF.Max(1f, multiplier), MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(multiplied, 0d, int.MaxValue);
    }

    private void UpdateBonusStateAfterRoll(GambaSettings gamba, bool isWin)
    {
        if (current is null) return;

        if (isWin)
        {
            current.ConsecutiveLosses = 0;
            return;
        }

        if (current.LossStreakBonusActive && gamba.LossStreakBonusDurationTurns.HasValue)
        {
            current.LossStreakBonusTurnsRemaining--;
            if (current.LossStreakBonusTurnsRemaining <= 0)
            {
                status = $"{gamba.LossStreakBonusName} expired after {FormatTurnCount(gamba.LossStreakBonusDurationTurns)} without a win.";
                current.LossStreakBonusActive = false;
                current.LossStreakBonusTurnsRemaining = 0;
                current.ConsecutiveLosses = 0;
            }
        }

        current.ConsecutiveLosses++;

        if (current.BartenderRollBonusActive && gamba.BartenderRollBonusDurationTurns.HasValue)
        {
            current.BartenderRollBonusTurnsRemaining--;
            if (current.BartenderRollBonusTurnsRemaining <= 0)
            {
                status = $"{gamba.BartenderRollBonusName} expired after {FormatTurnCount(gamba.BartenderRollBonusDurationTurns)} without a win.";
                current.BartenderRollBonusActive = false;
                current.BartenderRollBonusTurnsRemaining = 0;
            }
        }

        if (gamba.LossStreakBonusEnabled && !current.LossStreakBonusActive && !current.BartenderRollBonusActive)
        {
            var threshold = Math.Max(1, gamba.LossStreakThreshold);
            if (current.ConsecutiveLosses >= threshold)
            {
                current.LossStreakBonusActive = true;
                current.LossStreakBonusTurnsRemaining = Math.Max(0, gamba.LossStreakBonusDurationTurns ?? 0);
                AnnounceBonusActivated(gamba.LossStreakBonusName, gamba.LossStreakBonusMultiplier, gamba.LossStreakBonusAnnouncement, FormatBonusDuration(gamba.LossStreakBonusDurationTurns));
            }
        }
    }

    private void DrawActiveBonusStatus(GambaSettings gamba)
    {
        if (!TryGetActiveBonusStatus(gamba, out var text, out var color))
            return;

        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    private void QueueAutomaticBartenderBonusRoll(GambaSettings gamba)
    {
        if (current is null || !gamba.BartenderRollBonusEnabled) return;

        queuedBartenderBonusRolls++;

        var now = DateTime.Now;
        // If the bartender is self-testing as the customer, the same local client has
        // just sent the customer /dice command. FFXIV can silently ignore an immediate
        // second /dice from the same client, so wait just long enough for the game dice
        // cooldown instead of marking the roll as sent too early. Real-customer rolls can
        // be answered almost immediately because the bartender did not just roll.
        var firstAllowedSend = resolvingCustomerRollWasLocalPlayer
            ? now.AddMilliseconds(1100)
            : now.AddMilliseconds(75);

        if (lastAutomaticBartenderBonusCommandAt != DateTime.MinValue)
            firstAllowedSend = MaxDate(firstAllowedSend, lastAutomaticBartenderBonusCommandAt.AddMilliseconds(1100));

        if (nextBartenderBonusCommandAt == DateTime.MinValue || nextBartenderBonusCommandAt < now)
            nextBartenderBonusCommandAt = firstAllowedSend;
        else
            nextBartenderBonusCommandAt = MinDate(nextBartenderBonusCommandAt, firstAllowedSend);

        var delaySeconds = Math.Max(0, (nextBartenderBonusCommandAt - now).TotalSeconds);
        status = delaySeconds >= 0.5
            ? $"Queued bartender bonus roll {queuedBartenderBonusRolls:N0}. BarManager will send /dice party {Math.Max(2, gamba.BartenderRollMax):N0} in about {delaySeconds:0.0}s."
            : $"Queued bartender bonus roll {queuedBartenderBonusRolls:N0}. BarManager will send /dice party {Math.Max(2, gamba.BartenderRollMax):N0} now.";
    }

    private static DateTime MaxDate(DateTime a, DateTime b) => a >= b ? a : b;
    private static DateTime MinDate(DateTime a, DateTime b) => a <= b ? a : b;

    private void TrySendQueuedBartenderBonusRoll(GambaSettings gamba)
    {
        if (current is null || !gamba.BartenderRollBonusEnabled)
        {
            queuedBartenderBonusRolls = 0;
            pendingBartenderBonusResults = 0;
            awaitingBartenderBonusRoll = false;
            return;
        }

        if (queuedBartenderBonusRolls <= 0 || DateTime.Now < nextBartenderBonusCommandAt)
            return;

        var max = Math.Max(2, gamba.BartenderRollMax);
        queuedBartenderBonusRolls--;
        pendingBartenderBonusResults++;
        awaitingBartenderBonusRoll = pendingBartenderBonusResults > 0;
        awaitingBartenderBonusRollSince = DateTime.Now;
        lastAutomaticBartenderBonusCommandAt = DateTime.Now;
        nextBartenderBonusCommandAt = DateTime.Now.AddMilliseconds(1100);
        status = $"Automatically sending /dice party {max} for bartender bonus. A roll of 1 activates {SafeBonusName(gamba.BartenderRollBonusName, "Bartender Bonus")}.";
        if (!chatCommands.Send($"/dice party {max}"))
        {
            pendingBartenderBonusResults = Math.Max(0, pendingBartenderBonusResults - 1);
            awaitingBartenderBonusRoll = pendingBartenderBonusResults > 0;
            nextBartenderBonusCommandAt = DateTime.Now.AddMilliseconds(1100);
            status = $"Could not send bartender bonus roll: {chatCommands.LastError}";
        }
        else
        {
            status = pendingBartenderBonusResults > 1
                ? $"Sent /dice party {max} for bartender bonus. Waiting for {pendingBartenderBonusResults:N0} bartender roll results."
                : $"Sent /dice party {max} for bartender bonus. Waiting for the bartender roll result.";
        }
    }

    private void ActivateBartenderBonus(GambaSettings gamba)
    {
        if (current is null || current.LossStreakBonusActive || current.BartenderRollBonusActive) return;

        current.BartenderRollBonusActive = true;
        current.BartenderRollBonusTurnsRemaining = Math.Max(0, gamba.BartenderRollBonusDurationTurns ?? 0);
        AnnounceBonusActivated(gamba.BartenderRollBonusName, gamba.BartenderRollBonusMultiplier, gamba.BartenderRollBonusAnnouncement, FormatBonusDuration(gamba.BartenderRollBonusDurationTurns));
        persistence.SaveNow();
    }


    private static string FormatBonusDuration(int? turns)
    {
        return turns.HasValue && turns.Value > 0
            ? $"for the next {turns.Value:N0} {(turns.Value == 1 ? "roll" : "rolls")} or until the next win"
            : "until your next win";
    }

    private static string FormatTurnCount(int? turns)
    {
        if (!turns.HasValue || turns.Value <= 0)
            return "unlimited turns";

        return $"{turns.Value:N0} {(turns.Value == 1 ? "turn" : "turns")}";
    }

    private void AnnounceBonusActivated(string configuredName, float multiplier, string template, string durationText = "until your next win")
    {
        if (current is null) return;
        var name = SafeBonusName(configuredName, "Bonus");
        var text = BuildBonusAnnouncement(template, current.CustomerName, name, MathF.Max(1f, multiplier), durationText);
        TryQueueAnnouncement(text);
    }

    private static string BuildBonusAnnouncement(string template, string player, string bonus, float multiplier, string durationText)
    {
        var text = string.IsNullOrWhiteSpace(template)
            ? "{player}, {bonus} is active! Your next win is multiplied by x{multiplier} {duration}."
            : template.Trim();

        return text
            .Replace("{player}", player)
            .Replace("{bonus}", bonus)
            .Replace("{multiplier}", multiplier.ToString("0.##"))
            .Replace("{duration}", durationText);
    }

    private static string SafeBonusName(string configuredName, string fallback) => string.IsNullOrWhiteSpace(configuredName) ? fallback : configuredName.Trim();

    private void HandleJackpotWin(GambaSettings gamba, int jackpotBefore, int payout)
    {
        if (current is null)
            return;

        if (gamba.JackpotShoutoutEnabled)
        {
            var shoutText = BuildJackpotShoutout(gamba, current.CustomerName, payout, jackpotBefore, config.ActiveVenue.Name);
            QueueChatCommand(BuildJackpotChatCommand(gamba.JackpotShoutoutChannel, shoutText));
        }

        if (gamba.AutoEndOnJackpotWin)
        {
            TryQueueAnnouncement($"{current.CustomerName}, stop rolling! You have won the jackpot!");
            rollsRemaining = 0;
            queuedBartenderBonusRolls = 0;
            pendingBartenderBonusResults = 0;
            awaitingBartenderBonusRoll = false;
            autoEndAfterBartenderBonusRoll = false;
            status = $"Jackpot won by {current.CustomerDisplay}. Session auto-ended and saved.";
            EndSession();
        }
    }

    private static string BuildJackpotShoutout(GambaSettings gamba, string player, int payout, int jackpot, string venue)
    {
        var template = string.IsNullOrWhiteSpace(gamba.JackpotShoutoutMessage)
            ? "Congratulations {player}! They just won the jackpot for {payout} gil!"
            : gamba.JackpotShoutoutMessage.Trim();

        return template
            .Replace("{player}", player)
            .Replace("{payout}", UiHelpers.Gil(Math.Max(0, payout)))
            .Replace("{jackpot}", UiHelpers.Gil(Math.Max(0, jackpot)))
            .Replace("{venue}", string.IsNullOrWhiteSpace(venue) ? "the venue" : venue.Trim());
    }

    private static string BuildJackpotChatCommand(string channel, string text)
    {
        var normalized = (channel ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant() switch
        {
            "s" or "say" => "say",
            "sh" or "shout" => "shout",
            "y" or "yell" => "yell",
            _ => "yell",
        };

        return $"/{normalized} {text}";
    }

    private int CalculateJackpotContribution(VenueProfile venue, GambaSettings gamba)
    {
        if (!gamba.AddRollPricePercentToJackpot || gamba.JackpotContributionPercent <= 0)
            return 0;

        var gambaDrink = venue.Drinks.FirstOrDefault(d => d.IsGambaDrink) ?? venue.Drinks.FirstOrDefault(d => d.Name.Equals(gamba.DrinkName, StringComparison.OrdinalIgnoreCase));
        var drinkPrice = Math.Max(0, gambaDrink?.Price ?? 0);
        var rollPrice = gamba.RollsPerDrink <= 0 ? drinkPrice : drinkPrice / (float)gamba.RollsPerDrink;
        return Math.Max(0, (int)MathF.Round(rollPrice * (gamba.JackpotContributionPercent / 100f)));
    }

    private void AnnounceRollsLeftIfNeeded(GambaSettings gamba)
    {
        SyncRollsRemainingFromSession();
        if (current is null || !gamba.AnnounceRollsLeft)
            return;

        if (rollsRemaining == lastAnnouncedRollsRemaining)
            return;

        var interval = Math.Clamp(gamba.AnnounceEveryRolls, 1, 50);
        if (rollsRemaining > 0 && rollsRemaining % interval != 0)
            return;

        var text = rollsRemaining <= 0
            ? $"{current.CustomerName}, you have no rolls remaining."
            : $"{current.CustomerName}, you have {rollsRemaining:N0} roll{(rollsRemaining == 1 ? string.Empty : "s")} remaining.";

        if (!TryQueueAnnouncement(text))
            return;

        lastAnnouncedRollsRemaining = rollsRemaining;
    }

    private bool TryQueueAnnouncement(string text)
    {
        lock (AnnouncementLock)
        {
            var now = DateTime.Now;
            if (text == lastGlobalAnnouncement && (now - lastGlobalAnnouncementAt).TotalSeconds < 10)
                return false;

            if (PendingGlobalAnnouncements.Contains(text))
                return false;

            PendingGlobalAnnouncements.Add(text);
            pendingPartyAnnouncements.Enqueue(text);
            return true;
        }
    }

    private bool QueueChatCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        lock (AnnouncementLock)
        {
            var now = DateTime.Now;
            if (command == lastGlobalAnnouncement && (now - lastGlobalAnnouncementAt).TotalSeconds < 10)
                return false;

            if (PendingGlobalAnnouncements.Contains(command))
                return false;

            PendingGlobalAnnouncements.Add(command);
            pendingChatCommands.Enqueue(command);
            return true;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (awaitingBartenderBonusRoll && pendingBartenderBonusResults > 0 && (DateTime.Now - awaitingBartenderBonusRollSince).TotalSeconds > 30)
        {
            pendingBartenderBonusResults = 0;
            awaitingBartenderBonusRoll = false;
            status = "Bartender bonus roll timed out.";
            if (autoEndAfterBartenderBonusRoll && queuedBartenderBonusRolls <= 0)
                EndSession();
        }

        TrySendQueuedBartenderBonusRoll(config.ActiveVenue.Gamba);

        if ((pendingPartyAnnouncements.Count == 0 && pendingChatCommands.Count == 0) || DateTime.Now < nextPartyAnnouncementAt)
            return;

        if (pendingChatCommands.Count > 0)
        {
            var command = pendingChatCommands.Dequeue();
            lock (AnnouncementLock)
            {
                PendingGlobalAnnouncements.Remove(command);
                var now = DateTime.Now;
                if (command == lastGlobalAnnouncement && (now - lastGlobalAnnouncementAt).TotalSeconds < 10)
                    return;

                lastGlobalAnnouncement = command;
                lastGlobalAnnouncementAt = now;
            }

            chatCommands.Send(command);
            nextPartyAnnouncementAt = DateTime.Now.AddMilliseconds(750);
            return;
        }

        var text = pendingPartyAnnouncements.Dequeue();
        lock (AnnouncementLock)
        {
            PendingGlobalAnnouncements.Remove(text);
            var now = DateTime.Now;
            if (text == lastGlobalAnnouncement && (now - lastGlobalAnnouncementAt).TotalSeconds < 10)
                return;

            lastGlobalAnnouncement = text;
            lastGlobalAnnouncementAt = now;
        }

        chatCommands.Send($"/p {text}");
        nextPartyAnnouncementAt = DateTime.Now.AddMilliseconds(750);
    }

    private void EndSession()
    {
        if (current is null) return;
        current.EndedAt = DateTime.Now;
        config.CurrentAudit.GambaSessions.Add(current);
        QueueSessionPayoutAnnouncement(current, config.ActiveVenue);
        status = $"Saved session for {current.CustomerDisplay}.";
        retainedRollHistory = current;
        current = null;
        rollsRemaining = 0;
        lastAnnouncedRollsRemaining = int.MinValue;
        awaitingBartenderBonusRoll = false;
        queuedBartenderBonusRolls = 0;
        pendingBartenderBonusResults = 0;
        nextBartenderBonusCommandAt = DateTime.MinValue;
        lastAutomaticBartenderBonusCommandAt = DateTime.MinValue;
        autoEndAfterBartenderBonusRoll = false;
        lastPartyRollFingerprint = string.Empty;
        lastPartyRollAt = DateTime.MinValue;
        customerName = string.Empty;
        customerWorld = string.Empty;
        drinks = 1;
        persistence.SaveNow();
    }

    private float CalculateSessionCardHeight(GambaSettings gamba, float availableWidth)
    {
        if (current is null)
            return AirTablet.UI.TabletAppTheme.Px(214f);

        var style = ImGui.GetStyle();
        var textLine = ImGui.GetTextLineHeight();
        var contentWidth = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(180f),
            availableWidth
            - style.WindowPadding.X * 2f
            - style.CellPadding.X * 2f
            - AirTablet.UI.TabletAppTheme.Px(8f));
        var height =
            style.WindowPadding.Y * 2f +
            style.ItemSpacing.Y +
            textLine +
            style.ItemSpacing.Y +
            (textLine * 4f) +
            (style.ItemSpacing.Y * 3f) +
            ImGui.GetFrameHeight() +
            AirTablet.UI.TabletAppTheme.Px(10f);

        if (TryGetActiveBonusStatus(gamba, out var bonusText, out _))
        {
            height +=
                ImGui.CalcTextSize(bonusText, false, contentWidth).Y +
                style.ItemSpacing.Y;
        }

        return MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(252f),
            MathF.Ceiling(height));
    }

    private static float CalculateManualCardHeight(
        bool allowPasteImport,
        string message,
        float availableWidth)
    {
        var style = ImGui.GetStyle();
        var contentWidth = MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(180f),
            availableWidth - style.WindowPadding.X * 2f);
        var messageHeight = MathF.Max(
            ImGui.GetTextLineHeight(),
            ImGui.CalcTextSize(message ?? string.Empty, false, contentWidth).Y);
        var height =
            style.WindowPadding.Y * 2f +
            style.ItemSpacing.Y +
            ImGui.GetTextLineHeight() +
            style.ItemSpacing.Y +
            ImGui.GetFrameHeight() +
            style.ItemSpacing.Y +
            messageHeight +
            ImGui.GetTextLineHeightWithSpacing() +
            AirTablet.UI.TabletAppTheme.Px(8f);

        if (allowPasteImport)
        {
            height +=
                ImGui.GetTextLineHeightWithSpacing() +
                AirTablet.UI.TabletAppTheme.Px(58f) +
                style.ItemSpacing.Y +
                AirTablet.UI.TabletAppTheme.Px(30f) +
                style.ItemSpacing.Y;
        }

        return MathF.Ceiling(height);
    }

    private static float CalculateHistoryCardHeight(
        int rollCount,
        float defaultHeight)
    {
        if (rollCount <= 0)
            return defaultHeight;

        var style = ImGui.GetStyle();
        var tableRowHeight =
            ImGui.GetTextLineHeight() +
            style.CellPadding.Y * 2f;
        var contentHeight =
            style.WindowPadding.Y * 2f +
            style.ItemSpacing.Y +
            ImGui.GetTextLineHeightWithSpacing() +
            AirTablet.UI.TabletAppTheme.Px(4f) +
            tableRowHeight * (rollCount + 1) +
            AirTablet.UI.TabletAppTheme.Px(12f);

        return MathF.Max(
            defaultHeight,
            MathF.Ceiling(contentHeight));
    }

    private bool TryGetActiveBonusStatus(
        GambaSettings gamba,
        out string text,
        out Vector4 color)
    {
        text = string.Empty;
        color = BarManagerTheme.Muted;
        if (current is null)
            return false;

        if (current.LossStreakBonusActive)
        {
            text = $"Active bonus: {SafeBonusName(gamba.LossStreakBonusName, "Loss Streak Bonus")} x{MathF.Max(1f, gamba.LossStreakBonusMultiplier):0.##} {FormatBonusDuration(gamba.LossStreakBonusDurationTurns)}";
            color = BarManagerTheme.Gold;
            return true;
        }

        if (current.BartenderRollBonusActive)
        {
            text = $"Active bonus: {SafeBonusName(gamba.BartenderRollBonusName, "Bartender Bonus")} x{MathF.Max(1f, gamba.BartenderRollBonusMultiplier):0.##} {FormatBonusDuration(gamba.BartenderRollBonusDurationTurns)}";
            color = BarManagerTheme.Gold;
            return true;
        }

        if (awaitingBartenderBonusRoll)
        {
            text = "Waiting for bartender bonus roll...";
            return true;
        }

        if (!gamba.LossStreakBonusEnabled)
            return false;

        text = $"Loss streak: {current.ConsecutiveLosses:N0}/{Math.Max(1, gamba.LossStreakThreshold):N0}";
        return true;
    }

    private void DrawCancelSessionConfirmation()
    {
        if (!pendingCancelSession || current is null)
            return;

        AirTablet.UI.TabletAppTheme.OpenCenteredModal("Cancel gamba session?##bar-manager-cancel-session");
        if (!AirTablet.UI.TabletAppTheme.BeginCenteredModal(
                "Cancel gamba session?##bar-manager-cancel-session",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() + AirTablet.UI.TabletAppTheme.Px(390f));
        ImGui.TextUnformatted(
            $"Cancel the active session for {current.CustomerDisplay}? Its {current.Rolls.Count:N0} resolved roll(s) and payout will not be saved.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (ImGui.Button(
                "Cancel session",
                AirTablet.UI.TabletAppTheme.Px(new Vector2(130f, 0f))))
        {
            CancelSession();
            pendingCancelSession = false;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }
        ImGui.SameLine();
        if (ImGui.Button(
                "Keep session",
                AirTablet.UI.TabletAppTheme.Px(new Vector2(120f, 0f))))
        {
            pendingCancelSession = false;
            AirTablet.UI.TabletAppTheme.CloseCenteredModal();
        }

        AirTablet.UI.TabletAppTheme.EndCenteredModal();
    }

    private void CancelSession()
    {
        current = null;
        rollsRemaining = 0;
        lastAnnouncedRollsRemaining = int.MinValue;
        lastPartyRollFingerprint = string.Empty;
        awaitingBartenderBonusRoll = false;
        queuedBartenderBonusRolls = 0;
        pendingBartenderBonusResults = 0;
        nextBartenderBonusCommandAt = DateTime.MinValue;
        lastAutomaticBartenderBonusCommandAt = DateTime.MinValue;
        autoEndAfterBartenderBonusRoll = false;
        status = "Active session cancelled.";
    }

    private void QueueSessionPayoutAnnouncement(GambaSessionRecord session, VenueProfile venue)
    {
        var payout = Math.Max(0, session.TotalPayout);
        if (session.JackpotWon && !venue.Gamba.ShowRollPurchaseGuidanceAfterJackpotWin)
        {
            TryQueueAnnouncement($"{session.CustomerName}, congratulations! You won the jackpot with a session payout of {UiHelpers.Gil(payout)}!");
            return;
        }

        var rollPrice = CalculateGambaRollPrice(venue);
        var totalBuyIn = CalculateSessionBuyIn(session, venue, rollPrice);
        var extraRolls = rollPrice > 0 ? payout / rollPrice : 0;
        var extraCashout = rollPrice > 0 ? payout % rollPrice : payout;
        var sameSessionCashout = Math.Max(0, payout - totalBuyIn);
        var buyBackGil = Math.Max(0, totalBuyIn - payout);
        var sameRollText = session.RollsAllowed == 1 ? "roll" : "rolls";
        var extraRollText = extraRolls == 1 ? "roll" : "rolls";

        string text;
        if (rollPrice <= 0)
        {
            text = $"{session.CustomerName}, your session payout is {UiHelpers.Gil(payout)}!";
        }
        else if (payout >= totalBuyIn && session.RollsAllowed > 0)
        {
            text = $"{session.CustomerName}, your session payout is {UiHelpers.Gil(payout)}! That is enough for another {session.RollsAllowed:N0} {sameRollText} plus {UiHelpers.Gil(sameSessionCashout)} cashout.";
        }
        else
        {
            text = $"{session.CustomerName}, your session payout is {UiHelpers.Gil(payout)}! That is enough for {extraRolls:N0} more {extraRollText} plus {UiHelpers.Gil(extraCashout)} cashout, or {UiHelpers.Gil(buyBackGil)} more gil to buy another {session.RollsAllowed:N0} {sameRollText}.";
        }

        TryQueueAnnouncement(text);
    }

    private static int CalculateSessionBuyIn(GambaSessionRecord session, VenueProfile venue, int rollPrice)
    {
        var gambaDrink = FindGambaDrink(venue);
        if (gambaDrink is not null && gambaDrink.Price > 0 && session.DrinksPurchased > 0)
            return Math.Max(0, gambaDrink.Price * session.DrinksPurchased);

        return Math.Max(0, rollPrice * session.RollsAllowed);
    }

    private static int CalculateGambaRollPrice(VenueProfile venue)
    {
        var gambaDrink = FindGambaDrink(venue);
        if (gambaDrink is null || gambaDrink.Price <= 0)
            return 0;

        var rollsPerDrink = Math.Max(1, venue.Gamba.RollsPerDrink);
        return Math.Max(1, (int)MathF.Ceiling(gambaDrink.Price / (float)rollsPerDrink));
    }

    private static DrinkDefinition? FindGambaDrink(VenueProfile venue)
    {
        return venue.Drinks.FirstOrDefault(d => d.IsGambaDrink)
            ?? venue.Drinks.FirstOrDefault(d => d.Name.Equals(venue.Gamba.DrinkName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.IsHandled || current is null)
            return;

        var gamba = config.ActiveVenue.Gamba;
        var sender = StripChatNoise(message.Sender.ToString());
        var body = StripChatNoise(message.Message.ToString());

        // Dice lines can arrive through different Dalamud chat kinds depending on
        // whether they were sent with /dice, /dice party, cross-world party, or the
        // local chat filters. Do not hard-require XivChatType.Party here; instead,
        // parse only actual Random! dice lines and then validate the sender.
        if (!body.Contains("Random!", StringComparison.OrdinalIgnoreCase)
            && !sender.Contains("Random!", StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryParsePartyRandom(sender, body, out var parsed))
            return;

        if (!LooksLikeRealDiceMessage(message))
        {
            status = $"Ignored dice-looking chat text because it did not contain the game's dice/autotranslate payloads. Payloads: {GetMessagePayloadSummary(message)}";
            return;
        }

        var isCurrentCustomer = MatchesCurrentCustomer(parsed.Name, parsed.World);
        var isLocalPlayer = MatchesLocalPlayer(parsed.Name, parsed.World);

        // Self-test support: when the bartender puts their own character in the
        // customer box, both the customer roll and the bartender bonus roll come
        // from the same sender. Separate them by dice range before the customer
        // path, so /dice 999 still counts as the customer roll while
        // /dice party <bartender max> counts as the bartender bonus check.
        if (isLocalPlayer && gamba.BartenderRollBonusEnabled && IsPotentialBartenderBonusRoll(parsed, gamba))
        {
            var bartenderSettings = new GambaSettings { MinRoll = gamba.MinRoll, MaxRoll = Math.Max(gamba.MinRoll + 1, gamba.BartenderRollMax) };
            var bartenderRejection = ValidateDiceRange(parsed, bartenderSettings, parsed.Name);
            TryConsumeBartenderBonusRoll(parsed.Name, parsed.World, parsed.Roll, bartenderRejection, gamba);
            return;
        }

        // Normal customer roll path. This works for real customers and for
        // self-testing when the local player rolls the venue gamba dice range.
        if (isCurrentCustomer)
        {
            SyncRollsRemainingFromSession();
            if (rollsRemaining <= 0)
                return;

            var rejectionReason = ValidateDiceRange(parsed, gamba, parsed.Name);
            if (!string.IsNullOrWhiteSpace(rejectionReason))
            {
                status = rejectionReason;
                WarnInvalidCustomerRoll(parsed.Name, rejectionReason);
                return;
            }

            if (parsed.Roll < gamba.MinRoll || parsed.Roll > gamba.MaxRoll)
            {
                status = $"Ignored party-chat roll {parsed.Roll}; expected {gamba.MinRoll}-{gamba.MaxRoll}.";
                return;
            }

            if (IsDuplicatePartyRoll(parsed.Name, parsed.World, parsed.Roll, body))
                return;

            resolvingCustomerRollWasLocalPlayer = isLocalPlayer;
            try
            {
                ResolveRoll(parsed.Roll, "party chat");
            }
            finally
            {
                resolvingCustomerRollWasLocalPlayer = false;
            }
            return;
        }

        // Bartender bonus checks for the normal case where the bartender and
        // customer are different players. Ignore non-matching bartender dice
        // ranges here so a bad manual /dice party value does not consume the
        // pending bonus check.
        if (!gamba.BartenderRollBonusEnabled || !isLocalPlayer || !IsPotentialBartenderBonusRoll(parsed, gamba))
            return;

        var normalBartenderSettings = new GambaSettings { MinRoll = gamba.MinRoll, MaxRoll = Math.Max(gamba.MinRoll + 1, gamba.BartenderRollMax) };
        var normalBartenderRejection = ValidateDiceRange(parsed, normalBartenderSettings, parsed.Name);
        TryConsumeBartenderBonusRoll(parsed.Name, parsed.World, parsed.Roll, normalBartenderRejection, gamba);
    }


    private static bool LooksLikeRealDiceMessage(IHandleableChatMessage message)
    {
        // Do not trust visible chat text alone. Players can type text such as
        // "Random! 729" manually, but real game dice messages contain SeString
        // payloads/icons/autotranslate markers in the message body that plain
        // typed text does not contain.
        try
        {
            var sawPayload = false;
            foreach (var payload in message.Message.Payloads)
            {
                sawPayload = true;
                var typeName = payload.GetType().Name;
                if (typeName.Equals("TextPayload", StringComparison.OrdinalIgnoreCase))
                    continue;

                // The exact icon IDs can differ by client/API details, so keep the
                // gate structural: real dice messages have non-text payloads in the
                // message body, while fake typed "Random!" lines are only text.
                if (typeName.Contains("Icon", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("AutoTranslate", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // If Dalamud ever gives an empty payload list for the message body,
            // treat it as unsafe rather than accepting spoofable plain text.
            return sawPayload && false;
        }
        catch
        {
            return false;
        }
    }


    private static string GetMessagePayloadSummary(IHandleableChatMessage message)
    {
        try
        {
            var types = message.Message.Payloads
                .Select(p => p.GetType().Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            return types.Length == 0 ? "none" : string.Join(", ", types);
        }
        catch
        {
            return "unavailable";
        }
    }


    private bool IsPotentialBartenderBonusRoll(ParsedPartyRoll parsed, GambaSettings gamba)
    {
        var requiredMax = Math.Max(gamba.MinRoll + 1, gamba.BartenderRollMax);

        if (parsed.RangeMax.HasValue)
        {
            var rangeMin = parsed.RangeMin ?? gamba.MinRoll;
            return rangeMin == gamba.MinRoll && parsed.RangeMax.Value == requiredMax;
        }

        // Some dice outputs, especially party-targeted dice, may not include the
        // (1-#) range in the received chat line. When BarManager is already waiting
        // for a bartender bonus check, treat a local un-ranged roll within the
        // bartender bonus max as that pending bartender roll. This also fixes
        // self-testing where the bartender and customer are the same character.
        return (awaitingBartenderBonusRoll || pendingBartenderBonusResults > 0 || queuedBartenderBonusRolls > 0)
            && parsed.Roll >= gamba.MinRoll
            && parsed.Roll <= requiredMax;
    }

    private bool TryConsumeBartenderBonusRoll(string name, string world, int roll, string rejectionReason, GambaSettings gamba)
    {
        var wasAwaitingBartenderRoll = awaitingBartenderBonusRoll || pendingBartenderBonusResults > 0;
        if (!wasAwaitingBartenderRoll && queuedBartenderBonusRolls <= 0)
        {
            // Let the bartender manually run /dice party <configured max> after a customer roll
            // even if the automatic command failed to fire. It should only count when the
            // bartender bonus feature is enabled and a session is live.
            if (current is null || !gamba.BartenderRollBonusEnabled)
                return false;
        }

        if (wasAwaitingBartenderRoll && pendingBartenderBonusResults > 0 && (DateTime.Now - awaitingBartenderBonusRollSince).TotalSeconds > 30)
        {
            pendingBartenderBonusResults = 0;
            awaitingBartenderBonusRoll = false;
            wasAwaitingBartenderRoll = false;
            status = "Bartender bonus roll timed out.";
        }

        if (!MatchesLocalPlayer(name, world))
            return false;

        if (pendingBartenderBonusResults > 0)
            pendingBartenderBonusResults--;
        else if (!wasAwaitingBartenderRoll && queuedBartenderBonusRolls > 0)
            queuedBartenderBonusRolls--;
        awaitingBartenderBonusRoll = pendingBartenderBonusResults > 0;
        nextBartenderBonusCommandAt = DateTime.Now.AddMilliseconds(1100);

        if (!string.IsNullOrWhiteSpace(rejectionReason))
        {
            status = rejectionReason;
            return true;
        }

        if (roll == 1)
        {
            ActivateBartenderBonus(gamba);
            status = $"Bartender rolled 1. {SafeBonusName(gamba.BartenderRollBonusName, "Bartender Bonus")} activated.";
        }
        else
        {
            status = $"Bartender rolled {roll}; no bartender bonus activated.";
        }

        if (autoEndAfterBartenderBonusRoll && queuedBartenderBonusRolls <= 0 && pendingBartenderBonusResults <= 0)
            EndSession();

        return true;
    }

    private static bool MatchesLocalPlayer(string name, string world)
    {
        try
        {
            if (!DalamudServices.PlayerState.IsLoaded)
                return false;

            var localName = CleanName(DalamudServices.PlayerState.CharacterName);
            if (string.IsNullOrWhiteSpace(localName))
                return false;

            var expectedWorld = string.Empty;
            try { expectedWorld = DalamudServices.PlayerState.HomeWorld.Value.Name.ToString(); } catch { }
            return MatchesCharacter(localName, expectedWorld, name, world);
        }
        catch
        {
            return false;
        }
    }

    private bool IsDuplicatePartyRoll(string name, string world, int roll, string body)
    {
        var fingerprint = $"{CleanName(name).ToLowerInvariant()}|{CleanName(world).ToLowerInvariant()}|{roll}|{body}";
        var now = DateTime.Now;
        if (fingerprint == lastPartyRollFingerprint && (now - lastPartyRollAt).TotalSeconds < 2)
            return true;

        lastPartyRollFingerprint = fingerprint;
        lastPartyRollAt = now;
        return false;
    }

    private bool MatchesCurrentCustomer(string name, string world)
    {
        if (current is null)
            return false;

        return MatchesCharacter(current.CustomerName, current.CustomerWorld, name, world);
    }

    private static bool MatchesCharacter(string expectedName, string expectedWorld, string actualName, string actualWorld)
    {
        var expected = NormalizeCharacterPart(expectedName);
        var expectedHomeWorld = NormalizeCharacterPart(expectedWorld);
        var actual = NormalizeCharacterPart(actualName);
        var actualHomeWorld = NormalizeCharacterPart(actualWorld);

        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
            return false;

        if (!string.IsNullOrWhiteSpace(expectedHomeWorld))
        {
            actual = StripTrailingWorld(actual, expectedHomeWorld);
            actualHomeWorld = StripTrailingWorld(actualHomeWorld, expectedHomeWorld);
        }
        else
        {
            var split = SplitKnownWorldFromLabel(actualName);
            if (!string.IsNullOrWhiteSpace(split.World))
            {
                actual = NormalizeCharacterPart(split.Name);
                actualHomeWorld = NormalizeCharacterPart(split.World);
            }
        }

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(expectedHomeWorld)
            || string.IsNullOrWhiteSpace(actualHomeWorld)
            || actualHomeWorld.Equals(expectedHomeWorld, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripTrailingWorld(string value, string world)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(world))
            return value;

        var compactValue = RemoveSpaces(value);
        var compactWorld = RemoveSpaces(world);
        if (compactValue.Length <= compactWorld.Length || !compactValue.EndsWith(compactWorld, StringComparison.OrdinalIgnoreCase))
            return value;

        var suffixStart = value.Length - world.Length;
        if (suffixStart >= 0 && value.EndsWith(world, StringComparison.OrdinalIgnoreCase))
            return value[..suffixStart].Trim();

        var withoutSpaces = compactValue[..^compactWorld.Length];
        return withoutSpaces.Trim();
    }

    private static string NormalizeCharacterPart(string text) => RemoveSpaces(CleanName(text)).Trim();

    private static string RemoveSpaces(string text) => Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);

    private static bool TryParsePartyRandom(string sender, string body, out ParsedPartyRoll parsed)
    {
        parsed = default;

        var cleanSender = CleanChatText(sender);
        var cleanBody = CleanChatText(body);
        if (!cleanBody.Contains("Random!", StringComparison.OrdinalIgnoreCase)
            && !cleanSender.Contains("Random!", StringComparison.OrdinalIgnoreCase))
            return false;

        // Try the received body first, then the sender/body combinations. Different
        // client languages, chat filters, and payload shapes can place the character
        // label in either field, with or without parentheses, colons, or a world suffix.
        var candidates = new List<(string Text, string FallbackSender)>
        {
            (cleanBody, cleanSender),
            ($"{cleanSender} {cleanBody}".Trim(), cleanSender),
            ($"{cleanSender}: {cleanBody}".Trim(), cleanSender)
        };

        foreach (var (text, fallback) in candidates)
        {
            if (TryParseRandomCandidate(text, fallback, out parsed))
                return true;
        }

        return false;
    }

    private static bool TryParseRandomCandidate(string text, string fallbackSender, out ParsedPartyRoll parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Regex.Match(
            text,
            @"^(?<label>.*?)\s*Random!\s*(?:\((?<rangeMin>\d+)\s*-\s*(?<rangeMax>\d+)\)\s*)?(?<roll>\d{1,4})\b",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        var label = match.Groups["label"].Value;
        if (string.IsNullOrWhiteSpace(label))
            label = fallbackSender;

        var (name, world) = SplitNameAndWorld(label);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!int.TryParse(match.Groups["roll"].Value, out var roll))
            return false;

        int? rangeMin = null;
        int? rangeMax = null;
        if (match.Groups["rangeMax"].Success)
        {
            if (!int.TryParse(match.Groups["rangeMin"].Value, out var parsedMin) || !int.TryParse(match.Groups["rangeMax"].Value, out var parsedMax))
                return false;

            rangeMin = parsedMin;
            rangeMax = parsedMax;
        }

        parsed = new ParsedPartyRoll(name, world, roll, rangeMin, rangeMax);
        return true;
    }

    private static (string Name, string World) SplitNameAndWorld(string label)
    {
        var value = CleanChatText(label).Trim();
        value = value.Trim('(', ')', '[', ']', '{', '}', ':').Trim();
        value = Regex.Replace(value, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(value))
            return (string.Empty, string.Empty);

        var atIndex = value.IndexOf('@');
        if (atIndex < 0)
            atIndex = value.IndexOf('＠');

        if (atIndex > 0 && atIndex < value.Length - 1)
            return (CleanName(value[..atIndex]), CleanName(value[(atIndex + 1)..]));

        var split = SplitKnownWorldFromLabel(value);
        return (CleanName(split.Name), CleanName(split.World));
    }

    private static (string Name, string World) SplitKnownWorldFromLabel(string label)
    {
        var value = CleanChatText(label).Trim().Trim('(', ')', '[', ']', '{', '}', ':').Trim();
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return (string.Empty, string.Empty);

        foreach (var world in KnownWorlds.OrderByDescending(w => w.Length))
        {
            if (value.EndsWith($" {world}", StringComparison.OrdinalIgnoreCase))
                return (value[..^(world.Length + 1)].Trim(), world);

            var compactValue = RemoveSpaces(value);
            var compactWorld = RemoveSpaces(world);
            if (compactValue.Length > compactWorld.Length && compactValue.EndsWith(compactWorld, StringComparison.OrdinalIgnoreCase))
            {
                var nameCompact = compactValue[..^compactWorld.Length];
                if (!string.IsNullOrWhiteSpace(nameCompact))
                    return (RestoreNameSpacing(value, world), world);
            }
        }

        return (value, string.Empty);
    }

    private static string RestoreNameSpacing(string value, string world)
    {
        if (value.EndsWith(world, StringComparison.OrdinalIgnoreCase))
            return value[..^world.Length].Trim();

        var compactWorld = RemoveSpaces(world);
        var compactValue = RemoveSpaces(value);
        if (compactValue.Length <= compactWorld.Length)
            return value;

        var compactNameLength = compactValue.Length - compactWorld.Length;
        var kept = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
                kept++;

            if (kept >= compactNameLength)
                return value[..(i + 1)].Trim();
        }

        return value;
    }

    private static string ValidateDiceRange(ParsedPartyRoll parsed, GambaSettings gamba, string displayName)
    {
        var requiredMax = Math.Max(gamba.MinRoll + 1, gamba.MaxRoll);

        if (!parsed.RangeMax.HasValue)
        {
            // Plain /dice is the same as /dice 999 in FFXIV. Only accept an
            // un-ranged dice line when the venue actually requires 999. Venues
            // using /dice 100, /dice 400, etc. must require the bracketed range
            // so players cannot use the default 999 roll by mistake or to cheat.
            return requiredMax == 999
                ? string.Empty
                : $"Ignored party-chat roll from {displayName}; plain /dice is treated as /dice 999, but this venue requires /dice {requiredMax}.";
        }

        var rangeMin = parsed.RangeMin ?? gamba.MinRoll;
        var rangeMax = parsed.RangeMax.Value;
        if (rangeMin != gamba.MinRoll || rangeMax != requiredMax)
            return $"Ignored party-chat roll from {displayName}; expected /dice {requiredMax} range ({gamba.MinRoll}-{requiredMax}), but saw ({rangeMin}-{rangeMax}).";

        return string.Empty;
    }

    private void WarnInvalidCustomerRoll(string playerName, string rejectionReason)
    {
        if (current is null)
            return;

        var name = string.IsNullOrWhiteSpace(playerName) ? current.CustomerName : playerName;
        var message = $"{name}, that roll was not counted. {rejectionReason}";
        TryQueueAnnouncement(message);
    }

    private void UseCurrentTarget()
    {
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter pc)
        {
            status = "No player target selected.";
            return;
        }

        customerName = pc.Name.ToString();
        try { customerWorld = pc.HomeWorld.Value.Name.ToString(); }
        catch { customerWorld = string.Empty; }
        status = string.IsNullOrWhiteSpace(customerWorld)
            ? $"Selected target {customerName}."
            : $"Selected target {customerName}@{customerWorld}.";
    }

    private static Vector4 GetTierColor(string tier) => tier switch
    {
        "JACKPOT" => BarManagerTheme.Gold,
        "HIGH" => new Vector4(0.88f, 0.48f, 0.94f, 1f),
        "MID" => new Vector4(0.49f, 0.72f, 0.94f, 1f),
        "LOW" => new Vector4(0.49f, 0.92f, 0.82f, 1f),
        "SO_CLOSE" => new Vector4(0.94f, 0.63f, 0.25f, 1f),
        _ => BarManagerTheme.Muted,
    };

    private static string StripChatNoise(string text) => CleanChatText(text);

    private static string CleanChatText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text.Replace('＠', '@');
        cleaned = Regex.Replace(cleaned, @"[\uE000-\uF8FF]", string.Empty);
        cleaned = Regex.Replace(cleaned, @"[\u0000-\u001F\u007F]", string.Empty);
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Trim();
    }

    private static string CleanName(string text)
    {
        var cleaned = CleanChatText(text).Trim().Trim(':').Trim();
        if (cleaned.StartsWith("(") && cleaned.EndsWith(")") && cleaned.Length > 2)
            cleaned = cleaned[1..^1].Trim();

        cleaned = Regex.Replace(cleaned, @"^[^\p{L}\p{N}]+", string.Empty);
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s'\-]", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim();
    }

}
