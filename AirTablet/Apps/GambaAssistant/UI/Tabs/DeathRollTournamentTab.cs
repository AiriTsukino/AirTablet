using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.DeathRoll;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class DeathRollTournamentTab
{
    private readonly Configuration config;
    private readonly DeathRollTournamentService drt;
    private readonly ChatQueueService chat;
    private readonly LogService log;
    private readonly Action<bool> setBracketWindowOpen;
    private string manualName = string.Empty;
    private string manualWorld = string.Empty;

    public DeathRollTournamentTab(Configuration config, DeathRollTournamentService drt, ChatQueueService chat, LogService log, Action<bool> setBracketWindowOpen)
    {
        this.config = config;
        this.drt = drt;
        this.chat = chat;
        this.log = log;
        this.setBracketWindowOpen = setBracketWindowOpen;
    }

    public void Draw()
    {
        DrawConfigAndRoster();
        DrawActiveMatch();
    }

    public void DrawBracketTab()
    {
        if (config.DeathRoll.BracketWindowOpen)
        {
            UiHelpers.Card("Tournament Bracket", () =>
            {
                ImGui.TextDisabled("The bracket is open in its own window.");
                if (ImGui.Button("Focus / Reopen Bracket Window"))
                    setBracketWindowOpen(true);
                SameLineIfFits("Show Bracket Inline");
                if (ImGui.Button("Show Bracket Inline"))
                    setBracketWindowOpen(false);
            });
        }
        else
        {
            DrawBracket();
        }
    }

    public void DrawDetachedBracket()
    {
        DrawBracket();
    }

    public void DrawLogTab()
    {
        UiHelpers.Card("DRT Log", () =>
        {
            var drtEntries = log.Entries
                .Where(e => IsDrtLogEntry(e))
                .TakeLast(300)
                .ToList();
            var broadcastText = BuildLogText(drtEntries);
            var historyText = BuildTournamentHistoryText();

            if (ImGui.Button("Copy DRT Log"))
                ImGui.SetClipboardText(broadcastText);
            SameLineIfFits("Copy Tournament History");
            if (ImGui.Button("Copy Tournament History"))
                ImGui.SetClipboardText(historyText);
            ImGui.Spacing();
            ImGui.TextDisabled($"Showing latest {drtEntries.Count} DRT log entries.");

            DrawDisabledWrapped("This tab keeps DRT broadcasts, suppressed broadcast attempts, join activity, warnings, and match history in one spot. If chat broadcasts are disabled, attempted DRT broadcasts still appear here internally.");

            ImGui.Separator();
            ImGui.TextColored(GambaTheme.Gold, "Broadcast / System Log");
            if (string.IsNullOrWhiteSpace(broadcastText))
            {
                ImGui.TextDisabled("No DRT log entries yet.");
            }
            else
            {
                var logHeight = Math.Max(
                    AirTablet.UI.TabletAppTheme.Px(180f),
                    ImGui.GetContentRegionAvail().Y * 0.48f);
                ImGui.InputTextMultiline("##drt-broadcast-log", ref broadcastText, Math.Max(broadcastText.Length + 1024, 4096), new Vector2(-1, logHeight), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
            }

            ImGui.Separator();
            ImGui.TextColored(GambaTheme.Gold, "Tournament Match History");
            if (string.IsNullOrWhiteSpace(historyText))
            {
                ImGui.TextDisabled("No match history yet.");
            }
            else
            {
                var historyHeight = Math.Max(
                    AirTablet.UI.TabletAppTheme.Px(160f),
                    ImGui.GetContentRegionAvail().Y - AirTablet.UI.TabletAppTheme.Px(20f));
                ImGui.InputTextMultiline("##drt-match-history-log", ref historyText, Math.Max(historyText.Length + 1024, 4096), new Vector2(-1, historyHeight), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
            }
        });
    }

    public void DrawSettingsTab()
    {
        UiHelpers.Card("DRT Settings", () =>
        {
            ImGui.TextColored(GambaTheme.Gold, "Chat Output");
            DrawDisabledWrapped("Choose where automatic DRT prompts and winner messages are sent. You can disable all DRT broadcasts below for silent/background operation.");

            var current = NormalizeChannelLabel(config.DeathRoll.ChatChannel);
            if (ImGui.BeginCombo("Auto DRT chat channel", current))
            {
                DrawChannelOption("party", "/party");
                DrawChannelOption("say", "/say");
                DrawChannelOption("shout", "/shout");
                DrawChannelOption("yell", "/yell");
                ImGui.EndCombo();
            }

            ImGui.TextDisabled($"Current command prefix: {GetChannelCommand(config.DeathRoll.ChatChannel)}");

            var disableBroadcasts = config.DeathRoll.DisableChatBroadcasts;
            if (ImGui.Checkbox("Disable all DRT chat broadcasts", ref disableBroadcasts))
                config.DeathRoll.DisableChatBroadcasts = disableBroadcasts;
            UiHelpers.Tooltip("When enabled, DRT still tracks joins, rolls, brackets, winners, and match state, but sends no automatic chat messages at all.");

            var delay = config.DeathRoll.ChatBroadcastDelaySeconds;
            ImGui.SetNextItemWidth(AirTablet.UI.TabletAppTheme.Px(120f));
            if (ImGui.InputFloat("DRT broadcast delay seconds", ref delay, 0.1f, 0.5f, "%.1f"))
                config.DeathRoll.ChatBroadcastDelaySeconds = Math.Clamp(delay, 0.2f, 30f);
            UiHelpers.Tooltip("Delay between automatic DRT chat messages. Default: 1.5 seconds.");

            var announceNextTurn = config.DeathRoll.AnnounceNextTurnAfterRoll;
            if (ImGui.Checkbox("Announce next roll after each valid turn", ref announceNextTurn))
                config.DeathRoll.AnnounceNextTurnAfterRoll = announceNextTurn;
            UiHelpers.Tooltip("Off by default. When off, DRT only announces the expected roll command after a player rolls the wrong range.");

            var requireDiceParty = config.DeathRoll.RequireDiceRollsInPartyChat;
            if (ImGui.Checkbox("Require /dice rolls when DRT chat is party", ref requireDiceParty))
                config.DeathRoll.RequireDiceRollsInPartyChat = requireDiceParty;
            UiHelpers.Tooltip("On by default. When DRT output is set to /party, /random rolls are ignored and the player is warned to use /dice instead.");

            var zeroBehavior = NormalizeOpeningZeroRollBehavior(config.DeathRoll.OpeningZeroRollBehavior);
            if (ImGui.BeginCombo("Opening 0 roll behavior", GetOpeningZeroRollBehaviorLabel(zeroBehavior)))
            {
                DrawOpeningZeroRollOption("eliminate", "Eliminate player and end round");
                DrawOpeningZeroRollOption("skip", "Skip turn to other player");
                ImGui.EndCombo();
            }
            UiHelpers.Tooltip("Controls what happens if the first active death-roll command returns 0.");
        });
    }

    private static bool IsDrtLogEntry(LogEntry entry)
    {
        if (entry.Category == LogCategory.ChatQueue && entry.Message.Contains("DRT", StringComparison.OrdinalIgnoreCase))
            return true;

        return entry.Message.StartsWith("DRT", StringComparison.OrdinalIgnoreCase)
            || entry.Message.StartsWith("!join", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("DRT !join", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("death roll", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildTournamentHistoryText()
    {
        var sb = new StringBuilder();
        foreach (var match in drt.Tournament.AllMatches.OrderBy(m => m.RoundIndex).ThenBy(m => m.MatchIndex))
        {
            if (match.History.Count == 0)
                continue;

            sb.AppendLine(drt.GetMatchLabel(match));
            foreach (var line in match.History)
                sb.Append("  - ").AppendLine(line);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildLogText(IEnumerable<LogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
            sb.Append('[').Append(entry.Timestamp.ToString("T")).Append("] [").Append(entry.Category).Append("] ").AppendLine(entry.Message);
        return sb.ToString().TrimEnd();
    }

    private void DrawChannelOption(string value, string label)
    {
        var selected = string.Equals(NormalizeChannelLabel(config.DeathRoll.ChatChannel), label, StringComparison.OrdinalIgnoreCase);
        if (ImGui.Selectable(label, selected))
            config.DeathRoll.ChatChannel = value;
        if (selected)
            ImGui.SetItemDefaultFocus();
    }

    private void DrawOpeningZeroRollOption(string value, string label)
    {
        var selected = string.Equals(NormalizeOpeningZeroRollBehavior(config.DeathRoll.OpeningZeroRollBehavior), value, StringComparison.OrdinalIgnoreCase);
        if (ImGui.Selectable(label, selected))
            config.DeathRoll.OpeningZeroRollBehavior = value;
        if (selected)
            ImGui.SetItemDefaultFocus();
    }

    private static string NormalizeOpeningZeroRollBehavior(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "skip" or "skip turn" or "skip_turn" => "skip",
        _ => "eliminate",
    };

    private static string GetOpeningZeroRollBehaviorLabel(string value) => value switch
    {
        "skip" => "Skip turn to other player",
        _ => "Eliminate player and end round",
    };

    private static string NormalizeChannelLabel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "/say" or "say" or "/s" or "s" => "/say",
        "/shout" or "shout" or "/sh" or "sh" => "/shout",
        "/yell" or "yell" or "/y" or "y" => "/yell",
        "/party" or "party" or "/p" or "p" => "/party",
        _ => "/party",
    };

    private static string GetChannelCommand(string? value) => NormalizeChannelLabel(value);

    private string GetRollCommandLabel(int? max = null)
    {
        var command = NormalizeChannelLabel(config.DeathRoll.ChatChannel) == "/party" ? "/dice" : "/random";
        return max.HasValue ? $"{command} {max.Value}" : command;
    }

    private static void DrawDisabledWrapped(string text)
    {
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX()
            + MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(180f),
                ImGui.GetContentRegionAvail().X));
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private void DrawConfigAndRoster()
    {
        UiHelpers.Card("Tournament Setup", () =>
        {
            var maxPlayers = config.DeathRoll.MaxPlayers;
            if (ImGui.SliderInt("Maximum entrants", ref maxPlayers, 2, 64))
            {
                maxPlayers = Math.Clamp(maxPlayers, 2, 64);
                if (maxPlayers % 2 != 0) maxPlayers++;
                config.DeathRoll.MaxPlayers = Math.Clamp(maxPlayers, 2, 64);
            }
            UiHelpers.Tooltip("Maximum tournament size. Even numbers only, up to 64.");

            ImGui.TextDisabled($"Status: {drt.Tournament.Status} | Entrants: {drt.Tournament.Entrants.Count}/{config.DeathRoll.MaxPlayers}");
            DrawDisabledWrapped($"Death Roll starts each match with both players rolling {GetRollCommandLabel(drt.Tournament.SeedingMax)}. Higher seed roll goes first, then the first active roll uses plain {GetRollCommandLabel()} before later turns shrink the max until someone rolls 1.");
            ImGui.Separator();

            var setup = drt.Tournament.Status == DeathRollTournamentStatus.Setup;
            if (!setup) ImGui.BeginDisabled();
            if (ImGui.Button("Add Party Players"))
            {
                var added = drt.AddPartyMembers();
                log.Add(LogCategory.Info, $"DRT added {added} party entrant(s). It skips the dealer/local player.");
            }
            SameLineIfFits("Add Current Target");
            if (ImGui.Button("Add Current Target"))
            {
                if (!drt.TryAddCurrentTarget(out var reason))
                    log.Add(LogCategory.Warnings, reason);
            }
            UiHelpers.Tooltip("Adds the currently targeted player. If no readable target is selected, this falls back to adding your local character so you can enter yourself.");
            SameLineIfFits("Add Yourself");
            if (ImGui.Button("Add Yourself"))
            {
                if (!drt.TryAddLocalPlayer(out var reason))
                    log.Add(LogCategory.Warnings, reason);
            }
            UiHelpers.Tooltip("Adds your own Name@World to the tournament roster.");

            if (ImGui.Button("Broadcast !join"))
                drt.BroadcastJoinPrompt();
            UiHelpers.Tooltip("Broadcasts: Type !join in chat to join the DRT Tournament. While active, party DRT listens to party chat; other DRT channels listen to say, yell, and shout.");
            SameLineIfFits("Stop !join");
            if (ImGui.Button("Stop !join"))
                drt.StopJoinBroadcast();
            ImGui.Spacing();
            ImGui.TextDisabled(config.DeathRoll.JoinBroadcastActive ? "!join active" : "!join inactive");

            if (ImGui.BeginTable("##drt-manual-entry", 3, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 1.15f);
                ImGui.TableSetupColumn("world", ImGuiTableColumnFlags.WidthStretch, 0.9f);
                ImGui.TableSetupColumn("action", ImGuiTableColumnFlags.WidthFixed, AirTablet.UI.TabletAppTheme.Px(104f));
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##drt-manual-name", "Character name", ref manualName, 64);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##drt-manual-world", "World", ref manualWorld, 64);

                ImGui.TableNextColumn();
                if (ImGui.Button("Add Manual", new Vector2(-1f, 0f)))
                {
                    if (drt.AddEntrant(new PlayerIdentity(manualName.Trim(), manualWorld.Trim()), out var reason))
                    {
                        manualName = string.Empty;
                        manualWorld = string.Empty;
                    }
                    else
                        log.Add(LogCategory.Warnings, reason);
                }

                ImGui.EndTable();
            }

            if (!setup) ImGui.EndDisabled();

            DrawEntrants(setup);

            ImGui.Separator();
            if (setup)
            {
                if (UiHelpers.DisabledAwareButton("Start Tournament", drt.CanStart(out var reason), reason))
                    drt.StartTournament(out _);
                SameLineIfFits("Clear Entrants");
                if (ImGui.Button("Clear Entrants"))
                    drt.ClearEntrants();
            }
            else
            {
                if (UiHelpers.ConfirmingButton("Reset DRT", "Confirm Reset DRT", "This resets the Death Roll tournament bracket. Entrants are kept so you can restart quickly."))
                    drt.ResetTournament();
            }
        });
    }

    private static void SameLineIfFits(string nextLabel)
    {
        var style = ImGui.GetStyle();
        var nextWidth = ImGui.CalcTextSize(nextLabel).X + style.FramePadding.X * 2f;
        var contentRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + nextWidth <= contentRight)
            ImGui.SameLine();
    }

    private void DrawEntrants(bool setup)
    {
        ImGui.TextColored(GambaTheme.Gold, "Entrants");
        if (drt.Tournament.Entrants.Count == 0)
        {
            ImGui.TextDisabled("No entrants yet.");
            return;
        }

        var entrantsHeight = MathF.Min(
            AirTablet.UI.TabletAppTheme.Px(180f),
            AirTablet.UI.TabletAppTheme.Px(30f * drt.Tournament.Entrants.Count + 24f));
        entrantsHeight = MathF.Max(AirTablet.UI.TabletAppTheme.Px(96f), entrantsHeight);
        ImGui.BeginChild("##drt-entrants", new Vector2(0, entrantsHeight), true);
        for (var i = 0; i < drt.Tournament.Entrants.Count; i++)
        {
            var player = drt.Tournament.Entrants[i];
            ImGui.Text($"{i + 1}. {player.DisplayName}");
            if (player.Eliminated)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("Eliminated");
            }
            if (setup)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##drt-remove-{i}"))
                    drt.RemoveEntrant(player);
            }
        }
        ImGui.EndChild();
    }

    private void DrawActiveMatch()
    {
        UiHelpers.Card("Active Match", () =>
        {
            var active = drt.Tournament.ActiveMatch;
            if (active is null)
            {
                ImGui.TextDisabled(drt.Tournament.Status == DeathRollTournamentStatus.Complete ? "Tournament complete." : "No active match selected.");
                return;
            }

            ImGui.TextColored(GambaTheme.Gold, $"{drt.GetMatchLabel(active)}: {active.PlayerA?.DisplayName ?? "TBD"} vs {active.PlayerB?.DisplayName ?? "TBD"}");
            ImGui.Text($"Status: {active.Status}");
            ImGui.Text($"Current max: {active.CurrentMax}");
            ImGui.Text($"Current turn: {active.CurrentTurn?.DisplayName ?? "Not set"}");
            if (active.Status == DeathRollMatchStatus.SeedingRolls)
                ImGui.TextDisabled($"Seed rolls: {active.PlayerA?.DisplayName}={active.SeedRollA?.ToString() ?? "-"}, {active.PlayerB?.DisplayName}={active.SeedRollB?.ToString() ?? "-"}");

            if (active.Status == DeathRollMatchStatus.Waiting && ImGui.Button($"Prompt {GetRollCommandLabel(drt.Tournament.SeedingMax)} Seeding"))
                drt.PromptSeedingRolls(active);
            if (active.Status == DeathRollMatchStatus.SeedingRolls && ImGui.Button($"Re-prompt {GetRollCommandLabel(drt.Tournament.SeedingMax)}"))
                drt.PromptSeedingRolls(active);
            if (active.Status == DeathRollMatchStatus.Playing && ImGui.Button(active.FirstDeathRollTaken ? "Prompt Current Roll" : $"Prompt Opening {GetRollCommandLabel()}"))
                drt.PromptCurrentTurn(active);
        });
    }

    private static void DrawBracketPanLegend()
    {
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX()
            + MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(220f),
                ImGui.GetContentRegionAvail().X - AirTablet.UI.TabletAppTheme.Px(12f)));
        ImGui.TextDisabled("Bracket controls: right-click drag or middle-click drag to pan. Left-click a match to make it active. Scroll bars and mouse wheel still work.");
        ImGui.PopTextWrapPos();
    }

    private void DrawBracket()
    {
        UiHelpers.Card("Tournament Bracket", () =>
        {
            var bracketWindowOpen = config.DeathRoll.BracketWindowOpen;
            if (ImGui.Checkbox("Open bracket in separate window", ref bracketWindowOpen))
                setBracketWindowOpen(bracketWindowOpen);
            UiHelpers.Tooltip("Moves the zoomable DRT bracket into its own resizable window. The tournament controls stay in the Tournament tab.");

            var zoom = config.DeathRoll.BracketZoom;
            if (ImGui.SliderFloat("Bracket zoom", ref zoom, 0.25f, 2.00f, "%.2fx"))
                config.DeathRoll.BracketZoom = zoom;
            UiHelpers.Tooltip("Zoom the bracket view. At low zoom, boxes and text scale together.");

            DrawBracketPanLegend();

            if (drt.Tournament.Rounds.Count == 0)
            {
                ImGui.TextDisabled("Start a tournament to generate the bracket.");
                return;
            }

            var requestedScale =
                Math.Clamp(config.DeathRoll.BracketZoom, 0.25f, 2.0f)
                * AirTablet.UI.TabletAppTheme.Scale;
            var firstRoundMatches = drt.Tournament.Rounds.FirstOrDefault()?.Count ?? 1;
            var firstSideCount = Math.Max(1, (int)Math.Ceiling(firstRoundMatches / 2f));
            var branchDepth = Math.Max(1, drt.Tournament.Rounds.Count - 1);
            var availableWidth = MathF.Max(
                AirTablet.UI.TabletAppTheme.Px(260f),
                ImGui.GetContentRegionAvail().X - AirTablet.UI.TabletAppTheme.Px(24f));
            var unscaledWidth =
                branchDepth * (255f + 58f) * 2f
                + MathF.Max(260f, 255f * 1.35f)
                + 64f;
            var fitScale = availableWidth / unscaledWidth;
            var scale = MathF.Min(requestedScale, fitScale);
            var roundWidth = 255f * scale;
            var matchHeight = 104f * scale;
            var matchGap = 42f * scale;
            var columnGap = 58f * scale;
            var finalGap = Math.Max(260f * scale, roundWidth * 1.35f);
            var branchStride = roundWidth + columnGap;
            var leftBranchWidth = branchDepth * branchStride;
            var totalWidth = ImGui.GetContentRegionAvail().X;
            var rowStride = matchHeight + matchGap;
            var totalHeight = Math.Max(500f * scale, 90f * scale + firstSideCount * rowStride + 80f * scale);

            ImGui.BeginChild(
                "##drt-bracket-scroll",
                AirTablet.UI.TabletAppTheme.Px(new Vector2(0, 520f)),
                true);
            ImGui.SetWindowFontScale(scale);

            var origin = ImGui.GetCursorScreenPos() + new Vector2(22f * scale, 32f * scale);
            var draw = ImGui.GetWindowDrawList();
            var activeId = drt.Tournament.ActiveMatchId;
            var leftX0 = origin.X;
            var rightX0 = origin.X + leftBranchWidth + finalGap;
            var finalX = (leftX0 + (branchDepth - 1) * branchStride + roundWidth + rightX0) / 2f - roundWidth / 2f;
            var centerY = origin.Y + 44f * scale + ((firstSideCount - 1) * rowStride) / 2f;

            var boxes = BuildBracketLayout(origin, leftX0, rightX0, finalX, centerY, scale, roundWidth, matchHeight, rowStride, branchStride, firstSideCount);
            DrawBracketConnectors(draw, boxes, roundWidth, matchHeight, scale);

            foreach (var entry in boxes)
                DrawMatchBox(draw, entry.Match, entry.Position, new Vector2(roundWidth, matchHeight), scale, activeId);

            ApplyBracketCanvasPanning();

            ImGui.Dummy(new Vector2(totalWidth, totalHeight));
            ImGui.SetWindowFontScale(1f);
            ImGui.EndChild();
        });
    }

    private static void ApplyBracketCanvasPanning()
    {
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            return;

        // Left-click is reserved for selecting matches and normal window interaction.
        // Panning uses right-click or middle-click only, so the detached window does
        // not need a temporary pin/no-move lock.
        var wantsPan = ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f)
            || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f);

        if (!wantsPan)
            return;

        var delta = ImGui.GetIO().MouseDelta;
        if (delta.LengthSquared() <= 0.001f)
            return;

        ImGui.SetScrollY(Math.Max(0f, ImGui.GetScrollY() - delta.Y));
    }

    private List<BracketBox> BuildBracketLayout(Vector2 origin, float leftX0, float rightX0, float finalX, float centerY, float scale, float roundWidth, float matchHeight, float rowStride, float branchStride, int firstSideCount)
    {
        var boxes = new List<BracketBox>();
        var totalRounds = drt.Tournament.Rounds.Count;
        if (totalRounds == 0) return boxes;

        for (var ri = 0; ri < totalRounds - 1; ri++)
        {
            var round = drt.Tournament.Rounds[ri];
            AddSideRoundBoxes(boxes, round, ri, true, leftX0, origin.Y, scale, roundWidth, matchHeight, rowStride, branchStride, firstSideCount);
            AddSideRoundBoxes(boxes, round, ri, false, rightX0, origin.Y, scale, roundWidth, matchHeight, rowStride, branchStride, firstSideCount);
        }

        var finalRound = drt.Tournament.Rounds.LastOrDefault();
        var final = finalRound?.FirstOrDefault();
        if (final is not null)
            boxes.Add(new BracketBox(final, new Vector2(finalX, centerY - matchHeight / 2f), false, totalRounds - 1, 0));

        return boxes;
    }

    private void AddSideRoundBoxes(List<BracketBox> boxes, List<DeathRollMatch> round, int roundIndex, bool leftSide, float x0, float originY, float scale, float roundWidth, float matchHeight, float rowStride, float branchStride, int firstSideCount)
    {
        var sideMatches = GetSideMatches(round, leftSide).ToList();
        if (sideMatches.Count == 0) return;

        var branchDepth = Math.Max(1, drt.Tournament.Rounds.Count - 1);
        var visualRoundIndex = leftSide ? roundIndex : branchDepth - 1 - roundIndex;
        var x = x0 + visualRoundIndex * branchStride;
        var groupSize = MathF.Pow(2f, roundIndex);
        var spacing = rowStride * groupSize;
        var yOffset = 44f * scale + ((groupSize - 1f) * rowStride) / 2f;
        var title = leftSide ? $"Left {drt.GetStageName(roundIndex, true)}" : $"Right {drt.GetStageName(roundIndex, true)}";

        ImGui.GetWindowDrawList().AddText(new Vector2(x, originY + 6f * scale), ImGui.GetColorU32(GambaTheme.Gold), title);
        for (var i = 0; i < sideMatches.Count; i++)
        {
            var y = originY + yOffset + i * spacing;
            boxes.Add(new BracketBox(sideMatches[i], new Vector2(x, y), leftSide, roundIndex, i));
        }
    }

    private void DrawBracketConnectors(ImDrawListPtr draw, List<BracketBox> boxes, float roundWidth, float matchHeight, float scale)
    {
        var lineColor = ImGui.GetColorU32(GambaTheme.Border);
        var activeLineColor = ImGui.GetColorU32(GambaTheme.Gold);
        var thickness = Math.Max(1f, 1.6f * scale);

        foreach (var source in boxes.Where(b => b.RoundIndex < drt.Tournament.Rounds.Count - 1))
        {
            var target = FindConnectorTarget(boxes, source);
            if (target is null) continue;

            var targetBox = target.Value;
            var sourceCenterY = source.Position.Y + matchHeight / 2f;
            var targetCenterY = targetBox.Position.Y + matchHeight / 2f;
            var sourceRight = source.Position.X + roundWidth;
            var sourceLeft = source.Position.X;
            var targetRight = targetBox.Position.X + roundWidth;
            var targetLeft = targetBox.Position.X;
            var isActivePath = source.Match.Status == DeathRollMatchStatus.Complete && source.Match.Winner is not null;
            var color = isActivePath ? activeLineColor : lineColor;

            if (source.LeftSide)
            {
                var midX = sourceRight + 18f * scale;
                draw.AddLine(new Vector2(sourceRight, sourceCenterY), new Vector2(midX, sourceCenterY), color, thickness);
                draw.AddLine(new Vector2(midX, sourceCenterY), new Vector2(midX, targetCenterY), color, thickness);
                draw.AddLine(new Vector2(midX, targetCenterY), new Vector2(targetLeft, targetCenterY), color, thickness);
            }
            else
            {
                var midX = sourceLeft - 18f * scale;
                draw.AddLine(new Vector2(sourceLeft, sourceCenterY), new Vector2(midX, sourceCenterY), color, thickness);
                draw.AddLine(new Vector2(midX, sourceCenterY), new Vector2(midX, targetCenterY), color, thickness);
                draw.AddLine(new Vector2(midX, targetCenterY), new Vector2(targetRight, targetCenterY), color, thickness);
            }
        }
    }

    private BracketBox? FindConnectorTarget(List<BracketBox> boxes, BracketBox source)
    {
        var nextRoundIndex = source.RoundIndex + 1;
        var nextSideIndex = source.SideIndex / 2;
        var totalRounds = drt.Tournament.Rounds.Count;

        if (nextRoundIndex == totalRounds - 1)
            return boxes.FirstOrDefault(b => b.RoundIndex == totalRounds - 1);

        return boxes.FirstOrDefault(b => b.RoundIndex == nextRoundIndex && b.LeftSide == source.LeftSide && b.SideIndex == nextSideIndex);
    }

    private IEnumerable<DeathRollMatch> GetSideMatches(List<DeathRollMatch> round, bool leftSide)
    {
        if (round.Count <= 1)
            return leftSide ? round : Enumerable.Empty<DeathRollMatch>();

        var half = (int)Math.Ceiling(round.Count / 2f);
        return leftSide ? round.Take(half) : round.Skip(half);
    }

    private void DrawMatchBox(ImDrawListPtr draw, DeathRollMatch match, Vector2 min, Vector2 size, float scale, Guid? activeId)
    {
        var max = min + size;
        var isActive = activeId.HasValue && activeId.Value == match.Id;
        var isComplete = match.Status == DeathRollMatchStatus.Complete;
        var bg = isActive ? GambaTheme.PurpleActive : GambaTheme.PanelBg;
        var border = isActive ? GambaTheme.Gold : isComplete ? GambaTheme.Purple : GambaTheme.Border;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(bg), 8f * scale);
        draw.AddRect(min, max, ImGui.GetColorU32(border), 8f * scale, 0, isActive ? 2.4f : 1.1f);

        var pad = 9f * scale;
        var textX = min.X + pad;
        var textY = min.Y + pad;
        var maxChars = Math.Clamp((int)((size.X - pad * 2f) / Math.Max(3.8f, 7.2f * scale)), 4, 34);
        var line = Math.Max(9f, 18f * scale);

        draw.AddText(new Vector2(textX, textY), ImGui.GetColorU32(GambaTheme.Gold), TrimName(drt.GetMatchLabel(match), maxChars));
        draw.AddText(new Vector2(textX, textY + line), ImGui.GetColorU32(GambaTheme.Text), TrimName(match.PlayerA?.DisplayName ?? "TBD", maxChars));
        draw.AddText(new Vector2(textX, textY + line * 2f), ImGui.GetColorU32(GambaTheme.Text), TrimName(match.PlayerB?.DisplayName ?? "TBD", maxChars));

        var status = match.Status == DeathRollMatchStatus.Complete
            ? $"Winner: {match.Winner?.DisplayName ?? "-"}"
            : match.Status.ToString();
        draw.AddText(new Vector2(textX, textY + line * 3.2f), ImGui.GetColorU32(GambaTheme.MutedText), TrimName(status, maxChars));

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##drt-match-{match.Id}", size);
        if (ImGui.IsItemClicked() && match.PlayerA is not null && match.PlayerB is not null)
            drt.SetActiveMatch(match);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(match.PlayerA is null || match.PlayerB is null ? "This future bracket slot is waiting for winners." : "Click to make this bracket the active match.");
    }

    private readonly record struct BracketBox(DeathRollMatch Match, Vector2 Position, bool LeftSide, int RoundIndex, int SideIndex);

    private static string TrimName(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
