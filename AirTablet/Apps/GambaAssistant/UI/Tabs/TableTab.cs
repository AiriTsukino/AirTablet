using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class TableTab
{
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly ProfileService profiles;
    private readonly PartyService party;
    private readonly PlayerSessionService players;
    private readonly DealerLedgerService dealerLedger;
    private readonly DiceService dice;
    private readonly ChatQueueService chat;
    private readonly UndoService undo;
    private readonly LogService log;
    private readonly Dictionary<string, long> customBets = new();
    private readonly Dictionary<string, long> quickDeposits = new();
    private int pendingInitialDealCards;
    private bool dealerTurnStarted;
    private bool allBustRoundOverAnnounced;
    private double nextLivePartySyncTime;
    private string? lastAstrologianBeneficTurnKey;
    private double nextAstrologianBattleModeRefreshTime;

    public TableTab(Configuration config, BlackjackSession session, ProfileService profiles, PartyService party, PlayerSessionService players, DealerLedgerService dealerLedger, DiceService dice, ChatQueueService chat, OverlayService overlays, UndoService undo, LogService log)
    {
        this.config = config;
        this.session = session;
        this.profiles = profiles;
        this.party = party;
        this.players = players;
        this.dealerLedger = dealerLedger;
        this.dice = dice;
        this.chat = chat;
        this.undo = undo;
        this.log = log;
        overlays.SetBlackjackActionCallbacks(HitActiveHand, StandActiveHand, DoubleDownActiveHand, SplitActiveHand);
    }

    public void Draw()
    {
        AutoSyncLiveParty();
        QueueAstrologianBattleModeRefreshIfNeeded();
        DrawStatusHeader();
        DrawRoundControls();
        DrawDealerCard();
        DrawPlayerTable();
    }


    private void AutoSyncLiveParty()
    {
        if (chat.DemoMode || !party.IsLocalPlayerLoaded)
            return;

        var now = ImGui.GetTime();
        if (now < nextLivePartySyncTime)
            return;

        nextLivePartySyncTime = now + 1.0;
        players.SyncParty(party.GetPartyTableOrder());
    }

    private void DrawStatusHeader()
    {
        UiHelpers.Card("Table Status", () =>
        {
            ImGui.TextColored(GambaTheme.Gold, $"Round {session.Round.RoundNumber}");
            DrawWrappedDisabled($"Phase: {session.Round.Phase}");
            DrawWrappedDisabled($"Chat/Dice: {chat.Status}");
            DrawWrappedDisabled($"Dice pending/queued: {dice.QueuedCount}");
            if (dice.Pending is not null)
                DrawWrappedDisabled($"Waiting for visible dealer dice: {dice.Pending.Purpose}");

            DrawWrappedDisabled(chat.DemoMode
                ? "Party sync: Demo mode isolated"
                : $"Party sync: {party.LastLivePartyMemberCount} live party member(s)");

        });
    }

    private void DrawRoundControls()
    {
        UiHelpers.Card("Dealer Controls", () =>
        {
            DrawControlGroupHeader("Automation");
            if (ButtonWithTooltip("Pause", "Pause queued chat and dice automation without changing the current Blackjack round."))
                chat.Pause();
            DrawSameLineIfFits("Resume");
            if (ButtonWithTooltip("Resume", "Resume queued chat and dice automation."))
                chat.Resume();
            DrawSameLineIfFits("Panic Stop");
            if (ButtonWithTooltip("Panic Stop", "Stops queued party messages and dice commands, clears pending dice rolls, then pauses automation so the dealer can recover safely."))
            {
                chat.PanicClear();
                dice.ClearPendingAndQueued();
                pendingInitialDealCards = 0;
                dealerTurnStarted = false;
                allBustRoundOverAnnounced = false;
                lastAstrologianBeneficTurnKey = null;
                nextAstrologianBattleModeRefreshTime = 0;
            }
            DrawSameLineIfFits("Sync Party Now");
            if (ButtonWithTooltip("Sync Party Now", "Forces an immediate live party refresh. Demo mode remains isolated."))
            {
                players.SyncParty(party.GetPartyTableOrder());
                nextLivePartySyncTime = ImGui.GetTime() + 1.0;
            }

            DrawControlGroupHeader("Round Flow");
            if (DisabledButtonWithTooltip("Open Betting", session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands, "Betting can only open between hands.", "Open the betting window for the next Blackjack round."))
            {
                dealerTurnStarted = false;
                allBustRoundOverAnnounced = false;
                session.Round.Phase = BlackjackPhase.BettingOpen;
                var vipLimit = Math.Max(session.Rules.MaximumBet, profiles.ActiveProfile.VipMaximumBet);
                var vipSuffix = profiles.ActiveProfile.BlackjackVips.Count > 0 && vipLimit > session.Rules.MaximumBet
                    ? $" VIP max: {vipLimit:N0} gil."
                    : string.Empty;
                chat.EnqueueParty($"Betting is open for next round. Table limits: {session.Rules.MinimumBet:N0}-{session.Rules.MaximumBet:N0} gil.{vipSuffix}");
                log.Add(LogCategory.RoundFlow, $"Betting opened for internal Round {session.Round.RoundNumber}.");
            }

            DrawSameLineIfFits("Start Dealing");
            if (DisabledButtonWithTooltip("Start Dealing", session.Round.Phase == BlackjackPhase.BettingOpen && session.SessionPlayers.Any(p => p.BetConfirmed), "Confirm at least one valid bet first.", "Close betting and start rolling the initial Blackjack deal."))
                StartDealing();

            DrawSameLineIfFits("Close Betting");
            if (DisabledButtonWithTooltip("Close Betting", session.Round.Phase == BlackjackPhase.BettingOpen, "Betting is not currently open.", "Close betting without dealing. Any pending bets are returned to player banks."))
                CloseBetting();

            DrawSameLineIfFits("Settle Round");
            if (DisabledButtonWithTooltip("Settle Round", session.Round.Phase == BlackjackPhase.Settlement, "Settlement is available once player/dealer resolution is complete.", "Settle every active hand against the dealer and move to the between-hands cashout phase."))
                SettleRound();

            DrawControlGroupHeader("Broadcasts");
            if (DisabledButtonWithTooltip("Rebroadcast Turn", session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null && session.ActiveHand is not null, "A player hand must be active.", "Repeat the active player's turn/options and the visible dealer hand in party chat."))
                RebroadcastCurrentTurnAndDealer();

            DrawSameLineIfFits("Rebroadcast Banks");
            if (ButtonWithTooltip("Rebroadcast Banks", "Broadcast all current player banks in compact grouped messages."))
                RebroadcastCurrentBanks();

            DrawControlGroupHeader("Recovery");
            var hasUndo = undo.Actions.Count > 0;
            if (DisabledButtonWithTooltip($"Undo Last ({undo.Actions.Count})", hasUndo, "There are no reversible actions in the current undo stack.", "Undo the most recent dealer button/action and restore the Blackjack table state."))
                undo.TryUndoLast();

            DrawSameLineIfFits("Reset Night");
            var resetClicked = UiHelpers.ConfirmingButton("Reset Night", "Confirm Reset Night", "This clears the current-night banks, ledger, logs, round history, and active hands. Exports are not automatic.");
            UiHelpers.Tooltip("Clear the whole current-night Blackjack table state. Use this only when starting over.");
            if (resetClicked)
            {
                session.ResetNight();
                log.Clear();
                undo.Clear();
                pendingInitialDealCards = 0;
                dealerTurnStarted = false;
                allBustRoundOverAnnounced = false;
                lastAstrologianBeneficTurnKey = null;
                nextAstrologianBattleModeRefreshTime = 0;
            }

            var nextUndo = undo.Actions.FirstOrDefault();
            if (nextUndo is not null)
                DrawWrappedDisabled($"Next undo: {nextUndo.Label}");
        });
    }



    private void DrawDealerCard()
    {
        UiHelpers.Card("Dealer Hand / Bank", () =>
        {
            var ledger = dealerLedger.Ledger;
            ImGui.TextColored(GambaTheme.Gold, $"Dealer tracked gil: {dealerLedger.ExpectedDealerGil:N0} gil");
            DrawWrappedDisabled($"Starting {ledger.StartingGil:N0} + settled game P/L ({ledger.GameProfitLoss:N0}) + tips + adjustments");
            if (ledger.ActualEndingGil.HasValue)
                DrawWrappedDisabled($"Actual ending gil entered: {ledger.ActualEndingGil.Value:N0} gil | Difference: {dealerLedger.Difference.GetValueOrDefault():N0} gil");
            ImGui.Separator();
            DrawWrappedText($"Cards: {session.Round.DealerHand.CardText}");
            DrawWrappedText($"Total: {session.Round.DealerHand.TotalText}");
            if (session.Round.Phase == BlackjackPhase.DealerTurn)
                DrawWrappedDisabled(dealerTurnStarted ? "Dealer automation is resolving." : "Dealer turn is ready.");
            else if (session.Round.Phase == BlackjackPhase.Settlement && AllPlayerHandsAreTerminalLosses())
                DrawWrappedDisabled("All active players are bust/void. Dealer hand is skipped for this round.");
        });
    }

    private void DrawPlayerTable()
    {
        UiHelpers.Card("Party Table", () =>
        {
            DrawWrappedDisabled("Players are grouped into compact cards so action buttons wrap instead of clipping on smaller windows.");
            foreach (var p in session.SessionPlayers.OrderBy(p => p.PartySlot))
                DrawPlayerRow(p);
        });
    }

    private void DrawPlayerRow(PlayerSessionState p)
    {
        ImGui.PushID(p.Identity.ToString());
        ImGui.Separator();

        ImGui.TextColored(p.Status == PlayerStatus.Dealer ? GambaTheme.Gold : GambaTheme.Text, $"{p.PartySlot}. {p.DisplayName}");
        DrawWrappedDisabled($"Status: {p.Status}");
        DrawWrappedDisabled($"Bank: {p.Bank.Available:N0} available | {p.Bank.ActiveBet:N0} active bet | {p.Bank.TotalTracked:N0} total tracked");

        if (p.Status != PlayerStatus.Dealer)
        {
            var removeClicked = UiHelpers.ConfirmingButton($"Remove##remove-{p.Identity}", $"Confirm Remove##remove-{p.Identity}", "Remove this player from the table. Active unfinished hands are voided first.");
            UiHelpers.Tooltip("Remove this player from the Blackjack table. Use this when someone leaves or should no longer be tracked at the table.");
            if (removeClicked)
            {
                players.RemovePlayer(p, "Manually removed from Blackjack table");
                ImGui.PopID();
                return;
            }
        }

        if (p.Status == PlayerStatus.Dealer)
        {
            ImGui.PopID();
            return;
        }

        if (session.Round.Phase == BlackjackPhase.BettingOpen)
            DrawBettingControls(p);

        DrawHands(p);
        DrawActiveHandControls(p);
        ImGui.PopID();
    }

    private void DrawBettingControls(PlayerSessionState p)
    {
        ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(12f));
        DrawControlGroupHeader("Bet controls");

        if (DisabledButtonWithTooltip("Min Bet", p.Bank.Available >= session.Rules.MinimumBet, "Player does not have enough available bank for the table minimum.", "Reserve the configured table minimum as this player's bet."))
            ReserveBetWithUndo(p, session.Rules.MinimumBet, "Min bet");
        DrawSameLineIfFits("Max Bet");
        var isCurrentPartyMember = p.Status != PlayerStatus.LeftDisconnected;
        var playerMaximum = profiles.GetMaximumBetForPlayer(p.Identity, isCurrentPartyMember);
        var maxBetIncrement = Math.Min(
            Math.Max(0, playerMaximum - p.Bank.ActiveBet),
            p.Bank.Available);
        if (DisabledButtonWithTooltip(
                "Max Bet",
                maxBetIncrement >= session.Rules.MinimumBet,
                "The player has already reached their table limit or lacks enough available bank for another minimum bet.",
                isCurrentPartyMember && profiles.IsBlackjackVip(profiles.ActiveProfile, p.Identity)
                    ? $"Reserve up to this venue's VIP maximum of {playerMaximum:N0} gil."
                    : $"Reserve up to the standard maximum of {playerMaximum:N0} gil."))
        {
            ReserveBetWithUndo(p, maxBetIncrement, "Max bet");
        }
        DrawSameLineIfFits("Last Bet");
        if (DisabledButtonWithTooltip("Last Bet", p.Bank.LastBet > 0 && p.Bank.Available >= p.Bank.LastBet, "No last bet is available, or the player lacks available bank.", "Repeat this player's most recent confirmed bet."))
            ReserveBetWithUndo(p, p.Bank.LastBet, "Last bet");
        DrawSameLineIfFits("Recent Trade");
        if (DisabledButtonWithTooltip("Recent Trade", p.Bank.LastTradeAmount > 0 && p.Bank.Available >= p.Bank.LastTradeAmount, "No recent trade is available, or the player lacks available bank.", "Use the most recent detected trade amount as this player's bet."))
            ReserveBetWithUndo(p, p.Bank.LastTradeAmount, "Recent trade bet");
        DrawSameLineIfFits("Unbet");
        if (DisabledButtonWithTooltip("Unbet", p.BetConfirmed && p.Bank.ActiveBet > 0, "This player has no confirmed active bet to return.", "Return the active bet to the player's available bank before the hand starts."))
            UnbetWithUndo(p);

        var betKey = p.Identity.ToString();
        customBets.TryGetValue(betKey, out var customBet);
        ImGui.Spacing();
        DrawWrappedDisabled("Custom bet");
        ImGui.SetNextItemWidth(GetResponsiveInputWidth("Bet"));
        if (UiHelpers.InputGil($"Amount##custom-bet-{betKey}", ref customBet))
            customBets[betKey] = customBet;
        DrawSameLineIfFits("Bet");
        if (ButtonWithTooltip($"Bet##bet-{betKey}", "Reserve the entered custom amount as this player's bet."))
            ReserveBetWithUndo(p, customBet, "Custom bet");

        quickDeposits.TryGetValue(betKey, out var quickDeposit);
        ImGui.Spacing();
        DrawWrappedDisabled("Quick bank add");
        ImGui.SetNextItemWidth(GetResponsiveInputWidth("Add Bank"));
        if (UiHelpers.InputGil($"Amount##quick-bank-{betKey}", ref quickDeposit))
            quickDeposits[betKey] = quickDeposit;
        DrawSameLineIfFits("Add Bank");
        if (ButtonWithTooltip($"Add Bank##quick-add-bank-{betKey}", "Add the entered gil to this player's bank and record it in the dealer ledger as a manual buy-in fallback."))
        {
            players.AddBankDeposit(p, quickDeposit);
            var dealer = session.SessionPlayers.FirstOrDefault(x => x.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
            if (dealer is not null && quickDeposit > 0)
            {
                dealerLedger.RecordTrade(new TradeEntry
                {
                    From = p.Identity,
                    To = dealer.Identity,
                    Amount = quickDeposit,
                    Classification = TradeClassification.BuyInBankDeposit,
                    Phase = session.Round.Phase.ToString(),
                    Note = "Table quick bank add",
                    Manual = true
                });
            }
            log.Add(LogCategory.Trades, $"Quick bank add from Table tab: {p.DisplayName} +{quickDeposit:N0} gil.");
        }
        ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(12f));
    }

    private void DrawHands(PlayerSessionState p)
    {
        if (p.Hands.Count == 0)
        {
            DrawWrappedDisabled("No active hand.");
            return;
        }

        ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(12f));
        DrawControlGroupHeader("Hands");
        foreach (var h in p.Hands)
        {
            var marker = session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer == p && session.ActiveHand == h ? "▶ " : "  ";
            DrawWrappedText($"{marker}Hand {h.HandNumber}: {h.CardText} = {h.TotalText} | Bet {h.Bet:N0} | {HandStatus(h)}");
        }
        ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(12f));
    }

    private bool TryGetActivePlayerHand(out PlayerSessionState player, out BlackjackHand hand)
    {
        player = null!;
        hand = null!;

        if (session.Round.Phase != BlackjackPhase.PlayerTurns || session.ActivePlayer is not { } activePlayer || session.ActiveHand is not { } activeHand)
            return false;

        player = activePlayer;
        hand = activeHand;
        return true;
    }

    private void HitActiveHand()
    {
        if (!TryGetActivePlayerHand(out var player, out var hand))
        {
            log.Add(LogCategory.Warnings, "No active Blackjack hand is available to hit.");
            return;
        }

        RequestCard(player, hand, "Hit", undoable: true);
    }

    private void StandActiveHand()
    {
        if (!TryGetActivePlayerHand(out _, out var hand))
        {
            log.Add(LogCategory.Warnings, "No active Blackjack hand is available to stand.");
            return;
        }

        Stand(hand);
    }

    private void DoubleDownActiveHand()
    {
        if (!TryGetActivePlayerHand(out var player, out var hand))
        {
            log.Add(LogCategory.Warnings, "No active Blackjack hand is available to double down.");
            return;
        }

        DoubleDown(player, hand);
    }

    private void SplitActiveHand()
    {
        if (!TryGetActivePlayerHand(out var player, out var hand))
        {
            log.Add(LogCategory.Warnings, "No active Blackjack hand is available to split.");
            return;
        }

        Split(player, hand);
    }

    private void DrawActiveHandControls(PlayerSessionState p)
    {
        var active = session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer == p;
        if (!active || session.ActiveHand is not { } hand)
            return;

        var sm = new BlackjackStateMachine(session);
        var hitEnabled = IsActiveUnfinishedHand(p, hand);
        var hitReason = hitEnabled ? string.Empty : "Hit is only available for the active unfinished hand.";
        var standEnabled = hitEnabled;
        var standReason = standEnabled ? string.Empty : "Stand is only available for the active unfinished hand.";
        var doubleEnabled = sm.CanDouble(hand, out var doubleReason);
        var doubleShortfall = GetDoubleDownAdditionalGilNeeded(p, hand);
        if (!doubleEnabled && doubleShortfall > 0 && IsDoubleDownStructurallyLegal(hand, out _))
            doubleReason = $"Player needs an additional {doubleShortfall:N0} gil to double down.";
        var doubleLabel = doubleShortfall > 0 && IsDoubleDownStructurallyLegal(hand, out _)
            ? $"Double Down (+{doubleShortfall:N0})"
            : "Double Down";
        var splitEnabled = sm.CanSplit(hand, out var splitReason);
        var splitShortfall = GetSplitAdditionalGilNeeded(p, hand);
        if (!splitEnabled && splitShortfall > 0 && sm.CanSplitStructurally(hand, out _))
            splitReason = $"Player needs an additional {splitShortfall:N0} gil to split this pair.";
        var splitLabel = splitShortfall > 0 && sm.CanSplitStructurally(hand, out _)
            ? $"Split (+{splitShortfall:N0})"
            : "Split";

        ImGui.Indent(AirTablet.UI.TabletAppTheme.Px(12f));
        DrawControlGroupHeader("Active hand actions");

        if (DisabledButtonWithTooltip("Hit", hitEnabled, hitReason, "Roll one additional card for the active hand."))
            RequestCard(p, hand, "Hit", undoable: true);

        DrawSameLineIfFits("Stand");
        if (DisabledButtonWithTooltip("Stand", standEnabled, standReason, "Complete the active hand without drawing another card."))
            Stand(hand);

        DrawSameLineIfFits(doubleLabel);
        if (DisabledButtonWithTooltip(doubleLabel, doubleEnabled, doubleReason, doubleShortfall > 0
                ? $"Player must trade an additional {doubleShortfall:N0} gil before the dealer can complete the Double Down."
                : "Double the hand wager, roll exactly one more card, then stand."))
            DoubleDown(p, hand);

        DrawSameLineIfFits(splitLabel);
        if (DisabledButtonWithTooltip(splitLabel, splitEnabled, splitReason, splitShortfall > 0
                ? $"Player must trade an additional {splitShortfall:N0} gil before the dealer can split this matching pair."
                : "Split a matching pair into two separate hands with an additional matching wager."))
            Split(p, hand);

        ImGui.Unindent(AirTablet.UI.TabletAppTheme.Px(12f));
    }

    private static void DrawControlGroupHeader(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(GambaTheme.Gold, label);
    }

    private static bool ButtonWithTooltip(string label, string tooltip)
    {
        var clicked = ImGui.Button(label);
        UiHelpers.Tooltip(tooltip);
        return clicked;
    }

    private static bool DisabledButtonWithTooltip(string label, bool enabled, string disabledReason, string enabledTooltip)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = ImGui.Button(label);

        if (!enabled)
        {
            ImGui.EndDisabled();
            UiHelpers.Tooltip(disabledReason);
            return false;
        }

        UiHelpers.Tooltip(enabledTooltip);
        return clicked;
    }

    private static void DrawSameLineIfFits(string nextLabel)
    {
        var visibleLabel = VisibleLabel(nextLabel);
        var style = ImGui.GetStyle();
        var nextWidth = ImGui.CalcTextSize(visibleLabel).X + style.FramePadding.X * 2f;
        var rightEdgeIfSameLine = ImGui.GetItemRectMax().X + style.ItemSpacing.X + nextWidth;
        var contentRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (rightEdgeIfSameLine <= contentRight)
            ImGui.SameLine();
    }

    private static float GetResponsiveInputWidth(string nextButtonLabel)
    {
        var style = ImGui.GetStyle();
        var buttonWidth = ImGui.CalcTextSize(VisibleLabel(nextButtonLabel)).X + style.FramePadding.X * 2f;
        var available = ImGui.GetContentRegionAvail().X;
        var inlineWidth = available - buttonWidth - style.ItemSpacing.X;
        if (inlineWidth >= 150f)
            return MathF.Min(AirTablet.UI.TabletAppTheme.Px(220f), inlineWidth);
        return MathF.Max(
            AirTablet.UI.TabletAppTheme.Px(150f),
            MathF.Min(AirTablet.UI.TabletAppTheme.Px(220f), available));
    }

    private static string VisibleLabel(string label)
    {
        var marker = label.IndexOf("##", StringComparison.Ordinal);
        return marker >= 0 ? label[..marker] : label;
    }

    private static void DrawWrappedDisabled(string text)
    {
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX()
            + MathF.Max(AirTablet.UI.TabletAppTheme.Px(180f), ImGui.GetContentRegionAvail().X));
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private static void DrawWrappedText(string text)
    {
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX()
            + MathF.Max(AirTablet.UI.TabletAppTheme.Px(180f), ImGui.GetContentRegionAvail().X));
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
    }

    private bool IsActiveUnfinishedHand(PlayerSessionState player, BlackjackHand hand)
        => session.Round.Phase == BlackjackPhase.PlayerTurns
           && session.ActivePlayer == player
           && session.ActiveHand == hand
           && !hand.IsComplete
           && !hand.IsBusted
           && !hand.IsVoided;

    private string HandStatus(BlackjackHand hand)
    {
        if (hand.IsVoided) return "Voided";
        if (hand.IsBusted) return "Bust";
        if (IsNaturalByRules(hand)) return "Natural Blackjack";
        if (hand.IsComplete) return "Complete";
        return "Acting";
    }

    private bool IsNaturalByRules(BlackjackHand hand)
        => hand.IsNaturalBlackjack || (session.Rules.SplitTwentyOneCountsAsNatural && hand.IsSplitHand && hand.IsTwoCardTwentyOne);

    private void ReserveBetWithUndo(PlayerSessionState p, long amount, string label)
    {
        var beforeAvailable = p.Bank.Available;
        var beforeActive = p.Bank.ActiveBet;
        var beforeLastBet = p.Bank.LastBet;
        var beforeConfirmed = p.BetConfirmed;
        var beforeHands = p.Hands.Select(TableTab.CloneHand).ToList();

        var maximumBet = profiles.GetMaximumBetForPlayer(
            p.Identity,
            p.Status != PlayerStatus.LeftDisconnected);
        if (!players.TryReserveBet(p, amount, out var reason, maximumBet))
        {
            log.Add(LogCategory.Warnings, reason);
            return;
        }

        AnnounceBetPlaced(p);

        undo.Push($"{label} for {p.DisplayName}", () =>
        {
            p.Bank.Available = beforeAvailable;
            p.Bank.ActiveBet = beforeActive;
            p.Bank.LastBet = beforeLastBet;
            p.BetConfirmed = beforeConfirmed;
            p.Hands.Clear();
            p.Hands.AddRange(beforeHands.Select(TableTab.CloneHand));
        });
    }

    private void UnbetWithUndo(PlayerSessionState p)
    {
        var before = CaptureSnapshot();
        var amount = p.Bank.ActiveBet;
        p.Bank.Available += p.Bank.ActiveBet;
        p.Bank.ActiveBet = 0;
        p.BetConfirmed = false;
        p.Hands.Clear();
        p.Status = PlayerStatus.SittingOut;
        log.Add(LogCategory.RoundFlow, $"Returned {amount:N0} gil active bet to {p.DisplayName}; bet is no longer confirmed.");
        chat.EnqueueParty($"{p.DisplayName}'s bet has been returned before the hand starts. Bank: {p.Bank.Available:N0} gil.");
        undo.Push($"Unbet {p.DisplayName}", () => RestoreSnapshot(before));
    }


    private void AnnounceBetPlaced(PlayerSessionState player)
    {
        if (session.Round.Phase != BlackjackPhase.BettingOpen || player.Status == PlayerStatus.Dealer)
            return;

        var message = ApplyTemplate("BetPlaced", new Dictionary<string, string>
        {
            ["player"] = player.DisplayName,
            ["amount"] = player.Bank.ActiveBet.ToString("N0"),
            ["bet"] = player.Bank.ActiveBet.ToString("N0"),
            ["bank"] = player.Bank.Available.ToString("N0"),
        });

        chat.EnqueueParty(message);
        log.Add(LogCategory.ChatQueue, $"Queued bet announcement for {player.DisplayName}: {player.Bank.ActiveBet:N0} gil.");
    }

    private void StartDealing()
    {
        var before = CaptureSnapshot();
        dealerTurnStarted = false;
        allBustRoundOverAnnounced = false;
        lastAstrologianBeneficTurnKey = null;
        nextAstrologianBattleModeRefreshTime = 0;
        chat.EnqueueParty("Bets closed. Dealing next round.");
        QueueAstrologianAstralDraw();
        var roundPlayers = session.SessionPlayers
            .Where(p => p.BetConfirmed)
            .OrderBy(p => p.PartySlot)
            .ToList();

        foreach (var player in roundPlayers)
            NormalizePrimaryHandForNewDeal(player);

        session.Round = new BlackjackRound
        {
            RoundNumber = session.Round.RoundNumber,
            Phase = BlackjackPhase.Dealing,
            Players = roundPlayers,
            DealerHand = new BlackjackHand { HandNumber = 0 },
            ActivePlayerIndex = 0,
            ActiveHandIndex = 0
        };

        pendingInitialDealCards = (roundPlayers.Count * 2) + 1;

        if (session.Rules.InitialDealMode == BlackjackInitialDealMode.PlayerFullHandsThenDealer)
        {
            // Deal each player's complete starting hand first, announce that hand only
            // after both cards resolve, then deal the dealer's visible card last.
            foreach (var p in session.Round.Players)
            {
                RequestInitialPlayerFullHand(p, p.Hands[0]);
            }

            RequestDealerCard("Dealer visible card", CountInitialDealCardResolved, continueDealerTurn: false);
            log.Add(LogCategory.RoundFlow, "Started full-hand initial deal using visible /dice results.");
            undo.Push("Start dealing", () => RestoreSnapshot(before));
            return;
        }

        // Round-robin/table-order deal: one card to each active player, one visible
        // dealer card, then the second card to each active player. The dealer's
        // final/reveal card is still rolled later during Dealer Turn per venue rules.
        foreach (var p in session.Round.Players)
        {
            RequestCard(p, p.Hands[0], "Initial card 1", CountInitialDealCardResolved);
        }

        RequestDealerCard("Dealer visible card", CountInitialDealCardResolved, continueDealerTurn: false);

        foreach (var p in session.Round.Players)
            RequestCard(p, p.Hands[0], "Initial card 2", CountInitialDealCardResolved);

        log.Add(LogCategory.RoundFlow, "Started round-robin initial deal using visible /dice results.");
        undo.Push("Start dealing", () => RestoreSnapshot(before));
    }

    private void CloseBetting()
    {
        var before = CaptureSnapshot();
        if (!players.CloseBetting("Betting closed manually by the dealer"))
            return;

        pendingInitialDealCards = 0;
        dealerTurnStarted = false;
        allBustRoundOverAnnounced = false;
        lastAstrologianBeneficTurnKey = null;
        nextAstrologianBattleModeRefreshTime = 0;
        chat.EnqueueParty("Betting is closed. Pending bets have been returned; no round will be dealt.");
        undo.Push("Close betting", () => RestoreSnapshot(before));
    }

    private static void NormalizePrimaryHandForNewDeal(PlayerSessionState player)
    {
        var bet = player.Bank.ActiveBet;
        player.Hands.Clear();
        player.Hands.Add(new BlackjackHand
        {
            HandNumber = 1,
            Bet = bet,
            OriginalBet = bet,
            IsComplete = false,
            IsBusted = false,
            IsDoubled = false,
            IsSplitHand = false,
            IsVoided = false
        });
    }

    private void RequestInitialPlayerFullHand(PlayerSessionState player, BlackjackHand hand)
    {
        var cardsResolvedForHand = 0;

        void ApplyInitialCard(BlackjackCard card, string reason)
        {
            hand.AddCard(card);
            hand.Actions.Add(reason);
            cardsResolvedForHand++;

            if (cardsResolvedForHand >= 2)
                AnnounceInitialPlayerHand(player, hand);

            CountInitialDealCardResolved();
        }

        dice.RequestRoll($"Initial card 1 for {player.DisplayName} Hand {hand.HandNumber}", c => ApplyInitialCard(c, "Initial card 1"));
        dice.RequestRoll($"Initial card 2 for {player.DisplayName} Hand {hand.HandNumber}", c => ApplyInitialCard(c, "Initial card 2"));
    }

    private void AnnounceInitialPlayerHand(PlayerSessionState player, BlackjackHand hand)
    {
        if (IsNaturalByRules(hand))
        {
            AnnounceNaturalBlackjack(player, hand);
            return;
        }

        chat.EnqueueParty($"{player.DisplayName} receives {hand.CardText}. Hand: {hand.CardText} = {hand.TotalText}.");
        if (hand.IsBusted)
            chat.EnqueueParty($"{player.DisplayName} busts with {hand.BestTotal}.");
    }

    private void QueueAstrologianAstralDraw()
    {
        if (!config.General.AstrologianAstralDrawOnDealEnabled)
            return;

        chat.EnqueueCommand("/battlemode on");
        chat.EnqueueCommand("/ac \"Astral Draw\"");
        QueueAstrologianBattleModeOn();
        log.Add(LogCategory.ChatQueue, "Astrologian Astral Draw queued for the initial deal.");
    }

    private void QueueAstrologianBattleModeOn()
    {
        chat.EnqueueCommand("/battlemode on");
        nextAstrologianBattleModeRefreshTime = ImGui.GetTime() + 8.0;
    }

    private void QueueAstrologianBattleModeRefreshIfNeeded()
    {
        if (!config.General.AstrologianKeepBattleModeOnEnabled || chat.Paused)
            return;

        if (session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands)
            return;

        var now = ImGui.GetTime();
        if (now < nextAstrologianBattleModeRefreshTime)
            return;

        QueueAstrologianBattleModeOn();
        log.Add(LogCategory.ChatQueue, "Astrologian battle mode refresh queued.");
    }

    private void QueueAstrologianBeneficForTurn(PlayerSessionState player, BlackjackHand hand)
    {
        if (!config.General.AstrologianImmersionEnabled || player.Status == PlayerStatus.Dealer)
            return;

        var turnKey = $"{session.Round.RoundNumber}|{player.Identity.Name}|{player.Identity.World}|{hand.HandNumber}";
        if (string.Equals(lastAstrologianBeneficTurnKey, turnKey, StringComparison.Ordinal))
            return;

        var targetCommand = BuildAstrologianTargetCommand(player);
        if (string.IsNullOrWhiteSpace(targetCommand))
            return;

        lastAstrologianBeneficTurnKey = turnKey;
        chat.EnqueueCommand(targetCommand, false, Math.Max(1.0f, config.General.ChatQueueDelaySeconds));
        chat.EnqueueCommand("/ac \"Benefic\" <t>");
        QueueAstrologianBattleModeOn();
        log.Add(LogCategory.ChatQueue, $"Astrologian target and Benefic queued for {player.DisplayName} {GetHandLabel(player, hand)}.");
    }

    private static string BuildAstrologianTargetCommand(PlayerSessionState player)
    {
        if (player.PartySlot is >= 2 and <= 8)
            return $"/target <{player.PartySlot}>";

        var targetName = GetTargetableCharacterName(player);
        return string.IsNullOrWhiteSpace(targetName) ? string.Empty : $"/target \"{targetName}\"";
    }

    private static string GetTargetableCharacterName(PlayerSessionState player)
    {
        var displayName = player.DisplayName;
        var atIndex = displayName.IndexOf('@');
        return atIndex > 0 ? displayName[..atIndex] : displayName;
    }

    private void CountInitialDealCardResolved()
    {
        if (pendingInitialDealCards <= 0) return;
        pendingInitialDealCards--;
        if (pendingInitialDealCards == 0)
            CompleteInitialDealAndStartPlayerTurns();
    }

    private void CompleteInitialDealAndStartPlayerTurns()
    {
        session.Round.Phase = BlackjackPhase.PlayerTurns;
        MoveToFirstUnfinishedPlayerHandOrDealerTurn();
        log.Add(LogCategory.RoundFlow, "Initial deal complete. Player turns started.");
        if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null)
        {
            chat.EnqueueParty("Initial deal complete.");
            AnnounceActivePlayerTurn();
        }
        BeginDealerTurnIfReady();
    }

    private void RequestCard(PlayerSessionState p, BlackjackHand hand, string reason, Action? afterApply = null, bool undoable = false)
    {
        var before = undoable ? CaptureSnapshot() : null;

        dice.RequestRoll($"{reason} for {p.DisplayName} Hand {hand.HandNumber}", c =>
        {
            hand.AddCard(c);
            hand.Actions.Add(reason);

            if (IsNaturalByRules(hand))
            {
                AnnounceNaturalBlackjack(p, hand);
            }
            else
            {
                chat.EnqueueParty($"{p.DisplayName} receives {c}. Hand: {hand.CardText} = {hand.TotalText}.");
            }

            if (hand.IsBusted)
                chat.EnqueueParty($"{p.DisplayName} busts with {hand.BestTotal}.");

            var wasPlayerTurnBeforeAfterApply = session.Round.Phase == BlackjackPhase.PlayerTurns;
            afterApply?.Invoke();

            // Initial-deal callbacks can transition Dealing -> PlayerTurns inside
            // afterApply. CompleteInitialDealAndStartPlayerTurns already announces
            // the first active turn, so do not let the same dice callback announce it
            // again after the phase changes.
            if (!wasPlayerTurnBeforeAfterApply)
                return;

            if (session.Round.Phase == BlackjackPhase.PlayerTurns && hand.IsComplete)
            {
                new BlackjackStateMachine(session).AdvanceToNextHand();
                if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null)
                    AnnounceActivePlayerTurn();
                BeginDealerTurnIfReady();
            }
            else if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer == p && session.ActiveHand == hand && !hand.IsComplete)
            {
                AnnounceActivePlayerTurn();
            }
        });

        if (before is not null)
            undo.Push($"{reason} for {p.DisplayName} hand {hand.HandNumber}", () => RestoreSnapshot(before));
    }

    private void Stand(BlackjackHand hand)
    {
        var before = CaptureSnapshot();
        var previousPlayer = session.ActivePlayer;
        var previousHand = session.ActiveHand;

        hand.IsComplete = true;
        hand.Actions.Add("Stand");
        chat.EnqueueParty($"{session.ActivePlayer?.DisplayName ?? "Player"} stands on {hand.TotalText}.");
        new BlackjackStateMachine(session).AdvanceToNextHand();
        if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null
            && (session.ActivePlayer != previousPlayer || session.ActiveHand != previousHand))
            AnnounceActivePlayerTurn();
        BeginDealerTurnIfReady();

        undo.Push($"Stand hand {hand.HandNumber}", () => RestoreSnapshot(before));
    }

    private void DoubleDown(PlayerSessionState p, BlackjackHand hand)
    {
        var sm = new BlackjackStateMachine(session);
        if (!sm.CanDouble(hand, out var r)) { log.Add(LogCategory.Warnings, r); return; }

        var before = CaptureSnapshot();

        if (hand.OriginalBet <= 0)
            hand.OriginalBet = hand.Bet;

        var additionalBet = hand.Bet;
        p.Bank.Available -= additionalBet;
        p.Bank.ActiveBet += additionalBet;
        hand.Bet += additionalBet;
        hand.IsDoubled = true;
        chat.EnqueueParty($"{p.DisplayName} doubles down. Additional wager: {additionalBet:N0} gil. Total hand bet: {hand.Bet:N0} gil.");
        log.Add(LogCategory.RoundFlow, $"Double Down reserved an additional {additionalBet:N0} gil for {p.DisplayName}.");
        RequestCard(p, hand, "Double Down", () => hand.IsComplete = true);
        undo.Push($"Double Down for {p.DisplayName} hand {hand.HandNumber}", () => RestoreSnapshot(before));
    }

    private void Split(PlayerSessionState p, BlackjackHand hand)
    {
        var sm = new BlackjackStateMachine(session);
        if (!sm.CanSplit(hand, out var r)) { log.Add(LogCategory.Warnings, r); return; }

        var before = CaptureSnapshot();

        if (hand.OriginalBet <= 0)
            hand.OriginalBet = hand.Bet;

        var card = hand.Cards[1];
        hand.Cards.RemoveAt(1);
        hand.IsComplete = false;
        hand.IsBusted = false;
        hand.IsSplitHand = true;

        var newHand = new BlackjackHand { HandNumber = p.Hands.Count + 1, Bet = hand.Bet, OriginalBet = hand.OriginalBet, IsSplitHand = true };
        newHand.Cards.Add(card);

        p.Bank.Available -= hand.Bet;
        p.Bank.ActiveBet += hand.Bet;
        p.Hands.Add(newHand);

        log.Add(LogCategory.RoundFlow, $"Split {p.DisplayName} hand into Hand {hand.HandNumber} and Hand {newHand.HandNumber}.");
        chat.EnqueueParty($"{p.DisplayName} splits {hand.Cards[0]} + {card}. Additional split wager: {hand.Bet:N0} gil.");
        undo.Push($"Split {p.DisplayName} hand {hand.HandNumber}", () => RestoreSnapshot(before));

        RequestSplitCompletionCards(p, hand, newHand);
    }

    private void RequestSplitCompletionCards(PlayerSessionState player, BlackjackHand firstHand, BlackjackHand secondHand)
    {
        var remaining = 2;
        var splitAces = firstHand.Cards.Count == 1
                        && secondHand.Cards.Count == 1
                        && firstHand.Cards[0].IsAce
                        && secondHand.Cards[0].IsAce;

        void ApplySplitCard(BlackjackHand targetHand, BlackjackCard card)
        {
            targetHand.AddCard(card);
            if (session.Rules.SplitAcesOneCardOnly && splitAces)
                targetHand.IsComplete = true;
            targetHand.Actions.Add($"Split hand {targetHand.HandNumber} card");
            chat.EnqueueParty($"{player.DisplayName} receives {card} for Hand {targetHand.HandNumber}. Hand: {targetHand.CardText} = {targetHand.TotalText}.");
            if (targetHand.IsBusted)
                chat.EnqueueParty($"{player.DisplayName} Hand {targetHand.HandNumber} busts with {targetHand.BestTotal}.");

            remaining--;
            if (remaining > 0)
                return;

            if (session.Round.Phase == BlackjackPhase.PlayerTurns)
            {
                if (session.ActiveHand is { IsComplete: true })
                    new BlackjackStateMachine(session).AdvanceToNextHand();

                if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null)
                    AnnounceActivePlayerTurn();

                BeginDealerTurnIfReady();
            }
        }

        dice.RequestRoll($"Split completion card for {player.DisplayName} Hand {firstHand.HandNumber}", c => ApplySplitCard(firstHand, c));
        dice.RequestRoll($"Split completion card for {player.DisplayName} Hand {secondHand.HandNumber}", c => ApplySplitCard(secondHand, c));
    }

    private void RebroadcastCurrentTurnAndDealer()
    {
        if (session.Round.Phase != BlackjackPhase.PlayerTurns || session.ActivePlayer is not { } player || session.ActiveHand is not { } hand)
        {
            log.Add(LogCategory.Warnings, "No active player turn is available to rebroadcast.");
            return;
        }

        chat.EnqueueParty($"Current turn: {player.DisplayName} Hand {hand.HandNumber}: {hand.CardText} = {hand.TotalText}. Bet: {hand.Bet:N0} gil.");
        chat.EnqueueParty($"Dealer showing: {session.Round.DealerHand.CardText} = {session.Round.DealerHand.TotalText}.");
        AnnounceActivePlayerTurn(triggerTurnImmersion: false);
        log.Add(LogCategory.ChatQueue, $"Rebroadcast current turn for {player.DisplayName} Hand {hand.HandNumber}.");
    }

    private void RebroadcastCurrentBanks()
    {
        // Include every non-dealer tracked at the table, including players who
        // have cashed out to 0 gil. The old filter skipped CashedOut players,
        // which made the button look like it stopped working after a dealer
        // paid a player's bank back to 0.
        var entries = session.SessionPlayers
            .Where(p => p.Status != PlayerStatus.Dealer)
            .OrderBy(p => p.PartySlot)
            .Select(p => $"{p.DisplayName}: {p.Bank.Available:N0} gil")
            .ToList();

        if (entries.Count == 0)
        {
            log.Add(LogCategory.Warnings, "No player banks are available to rebroadcast.");
            return;
        }

        const int maxPerMessage = 3;
        for (var i = 0; i < entries.Count; i += maxPerMessage)
            chat.EnqueueParty("Current banks: " + string.Join(" | ", entries.Skip(i).Take(maxPerMessage)));

        log.Add(LogCategory.ChatQueue, $"Rebroadcast current player banks for {entries.Count} tracked player(s).");
    }

    private void AnnounceActivePlayerTurn(bool triggerTurnImmersion = true)
    {
        if (session.Round.Phase != BlackjackPhase.PlayerTurns || session.ActivePlayer is not { } player || session.ActiveHand is not { } hand || hand.IsComplete)
            return;

        if (triggerTurnImmersion)
            QueueAstrologianBeneficForTurn(player, hand);

        var options = BuildLegalOptions(hand);
        var handLabel = GetHandLabel(player, hand);
        var message = ApplyTemplate("PlayerTurnOptions", new Dictionary<string, string>
        {
            ["player"] = player.DisplayName,
            ["hand"] = hand.CardText,
            ["handLabel"] = handLabel,
            ["total"] = hand.TotalText,
            ["bet"] = hand.Bet.ToString("N0"),
            ["bank"] = player.Bank.Available.ToString("N0"),
            ["options"] = options,
        });

        message = ApplySplitHandLabelFallback(message, handLabel);

        chat.EnqueueParty(message);
        log.Add(LogCategory.RoundFlow, $"Turn options for {player.DisplayName} {handLabel}: {options}.");
    }

    private static string GetHandLabel(PlayerSessionState player, BlackjackHand hand)
    {
        var activeHandCount = player.Hands.Count(h => !h.IsVoided && h.Cards.Count > 0);
        return hand.IsSplitHand || activeHandCount > 1 ? $"Hand {hand.HandNumber}" : "Hand";
    }

    private static string ApplySplitHandLabelFallback(string message, string handLabel)
    {
        if (string.Equals(handLabel, "Hand", StringComparison.OrdinalIgnoreCase))
            return message;

        // Existing venue templates may still contain literal text like "Hand:" instead
        // of the new {handLabel} variable. Rewrite that common default wording so
        // split prompts clearly say Hand 1 / Hand 2 without forcing users to reset
        // their saved chat templates.
        return message
            .Replace("Current hand:", $"{handLabel}:", StringComparison.OrdinalIgnoreCase)
            .Replace("Hand:", $"{handLabel}:", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildLegalOptions(BlackjackHand hand)
    {
        var options = new List<string>();
        var sm = new BlackjackStateMachine(session);

        // Hit and Stand use the same legality rules as the UI buttons: they are
        // available only during Player Turns for the active unfinished hand.
        // Double and Split have additional rules handled by the state machine.
        if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActiveHand == hand && !hand.IsComplete)
        {
            options.Add("Hit");
            options.Add("Stand");
        }

        if (sm.CanDouble(hand, out _))
        {
            options.Add("Double Down");
        }
        else if (session.ActivePlayer is { } player && IsDoubleDownStructurallyLegal(hand, out _)
                 && GetDoubleDownAdditionalGilNeeded(player, hand) is var needed && needed > 0)
        {
            options.Add($"Double Down with additional {needed:N0} gil");
        }

        if (sm.CanSplit(hand, out _))
        {
            options.Add("Split");
        }
        else if (session.ActivePlayer is { } splitPlayer
                 && sm.CanSplitStructurally(hand, out _)
                 && GetSplitAdditionalGilNeeded(splitPlayer, hand) is var splitNeeded
                 && splitNeeded > 0)
        {
            options.Add($"Split with additional {splitNeeded:N0} gil");
        }

        return options.Count == 0 ? "No legal actions" : string.Join(", ", options);
    }

    private bool IsDoubleDownStructurallyLegal(BlackjackHand hand, out string reason)
    {
        reason = string.Empty;
        if (session.Round.Phase != BlackjackPhase.PlayerTurns || session.ActiveHand != hand || hand.IsComplete)
        {
            reason = "Double Down is only available for the active unfinished hand.";
            return false;
        }

        if (hand.Cards.Count != 2)
        {
            reason = "Double Down is only available on the first two cards by default.";
            return false;
        }

        if (hand.IsSplitHand && !session.Rules.DoubleAfterSplit)
        {
            reason = "Rules disable Double Down on split hands.";
            return false;
        }

        if (session.Rules.DoubleOnlyOnNineTenEleven && hand.BestTotal is not (9 or 10 or 11))
        {
            reason = "Rules restrict Double Down to totals of 9, 10, or 11.";
            return false;
        }

        return true;
    }

    private static long GetDoubleDownAdditionalGilNeeded(PlayerSessionState player, BlackjackHand hand)
    {
        return Math.Max(0, hand.Bet - player.Bank.Available);
    }

    private static long GetSplitAdditionalGilNeeded(PlayerSessionState player, BlackjackHand hand)
    {
        return Math.Max(0, hand.Bet - player.Bank.Available);
    }

    private void AnnounceNaturalBlackjack(PlayerSessionState player, BlackjackHand hand)
    {
        var message = ApplyTemplate("NaturalBlackjack", new Dictionary<string, string>
        {
            ["player"] = player.DisplayName,
            ["hand"] = hand.CardText,
            ["handLabel"] = GetHandLabel(player, hand),
            ["total"] = hand.TotalText,
            ["bet"] = hand.Bet.ToString("N0"),
            ["bank"] = player.Bank.Available.ToString("N0"),
            ["outcome"] = "Natural Blackjack",
        });

        var channel = profiles.ActiveProfile.NaturalBlackjackChatChannel;
        chat.EnqueueBlackjackToChannel(channel, message);
        log.Add(LogCategory.RoundFlow, $"{player.DisplayName} has a Natural Blackjack on {hand.CardText}; announcement queued to {NormalizeChatChannel(channel)}.");
    }

    private static string NormalizeChatChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "/say" or "say" or "/s" or "s" => "/say",
        "/shout" or "shout" or "/sh" or "sh" => "/shout",
        "/yell" or "yell" or "/y" or "y" => "/yell",
        "/party" or "party" or "/p" or "p" => "/party",
        _ => "/party",
    };

    private string ApplyTemplate(string key, IReadOnlyDictionary<string, string> values)
    {
        var profile = profiles.ActiveProfile;
        if (!profile.ChatTemplates.TryGetValue(key, out var template) || string.IsNullOrWhiteSpace(template))
            template = ChatTemplateDefaults.CreateFormal().TryGetValue(key, out var fallback) ? fallback : "{player}: Options: {options}.";

        foreach (var (name, value) in values)
            template = template.Replace("{" + name + "}", value, StringComparison.OrdinalIgnoreCase);
        return template;
    }

    private void MoveToFirstUnfinishedPlayerHandOrDealerTurn()
    {
        for (var pi = 0; pi < session.Round.Players.Count; pi++)
        {
            var player = session.Round.Players[pi];
            for (var hi = 0; hi < player.Hands.Count; hi++)
            {
                if (!player.Hands[hi].IsComplete)
                {
                    session.Round.ActivePlayerIndex = pi;
                    session.Round.ActiveHandIndex = hi;
                    return;
                }
            }
        }

        session.Round.Phase = BlackjackPhase.DealerTurn;
    }

    private void BeginDealerTurnIfReady()
    {
        if (session.Round.Phase != BlackjackPhase.DealerTurn || dealerTurnStarted)
            return;

        if (AllPlayerHandsAreTerminalLosses())
        {
            dealerTurnStarted = false;
            session.Round.DealerHand.IsComplete = true;
            session.Round.Phase = BlackjackPhase.Settlement;
            AnnounceAllBustRoundOver();
            return;
        }

        dealerTurnStarted = true;
        log.Add(LogCategory.RoundFlow, "All player hands complete. Dealer turn started.");
        chat.EnqueueParty("All player hands are complete. Dealer turn begins.");

        if (session.Round.DealerHand.Cards.Count < 2)
            RequestDealerCard("Dealer reveal card", continueDealerTurn: true);
        else
            ContinueDealerTurn();
    }

    private void AnnounceAllBustRoundOver()
    {
        if (allBustRoundOverAnnounced) return;
        allBustRoundOverAnnounced = true;

        var message = "All active players have bust. Round over; dealer hand is skipped.";
        if (chat.DemoMode)
            log.Add(LogCategory.Demo, message);
        else
            chat.EnqueueParty(message);
        log.Add(LogCategory.RoundFlow, message);
    }

    private bool AllPlayerHandsAreTerminalLosses()
    {
        var activeHands = session.Round.Players.SelectMany(p => p.Hands).Where(h => !h.IsVoided).ToList();
        return activeHands.Count > 0 && activeHands.All(h => h.IsBusted);
    }

    private void RequestDealerCard(string reason, Action? afterApply = null, bool continueDealerTurn = true)
    {
        dice.RequestRoll(reason, c =>
        {
            session.Round.DealerHand.AddCard(c);
            session.Round.DealerHand.Actions.Add(reason);
            chat.EnqueueParty($"Dealer draws {c}. Dealer hand: {session.Round.DealerHand.CardText} = {session.Round.DealerHand.TotalText}.");
            afterApply?.Invoke();
            if (continueDealerTurn)
                ContinueDealerTurn();
        });
    }

    private void ContinueDealerTurn()
    {
        if (session.Round.Phase != BlackjackPhase.DealerTurn)
            return;

        var dealer = session.Round.DealerHand;
        if (dealer.IsNaturalBlackjack)
        {
            session.Round.Phase = BlackjackPhase.Settlement;
            log.Add(LogCategory.RoundFlow, "Dealer has Natural Blackjack. Round is ready for settlement.");
            return;
        }

        if (ShouldDealerDraw(dealer))
        {
            RequestDealerCard("Dealer draw", continueDealerTurn: true);
            return;
        }

        dealer.IsComplete = true;
        session.Round.Phase = BlackjackPhase.Settlement;
        if (dealer.IsBusted)
        {
            chat.EnqueueParty($"Dealer busts with {dealer.BestTotal}. Round is ready for settlement.");
            log.Add(LogCategory.RoundFlow, $"Dealer busts with {dealer.BestTotal}. Round is ready for settlement.");
        }
        else
        {
            chat.EnqueueParty($"Dealer stands on {dealer.TotalText}. Round is ready for settlement.");
            log.Add(LogCategory.RoundFlow, $"Dealer stands on {dealer.TotalText}. Round is ready for settlement.");
        }
    }

    private bool ShouldDealerDraw(BlackjackHand dealer)
    {
        if (dealer.IsBusted) return false;
        if (dealer.BestTotal < 17) return true;
        if (dealer.BestTotal == 17 && dealer.IsSoft && !session.Rules.DealerStandsOnSoft17) return true;
        return false;
    }

    private void SettleRound()
    {
        if (AllPlayerHandsAreTerminalLosses())
            AnnounceAllBustRoundOver();

        var settlement = new BlackjackSettlementService(session.Rules);
        foreach (var p in session.Round.Players)
        {
            foreach (var h in p.Hands)
            {
                var entry = settlement.Settle(p, h, session.Round.DealerHand, session.Round.RoundNumber);
                p.RoundHistory.Add(entry);
                dealerLedger.ApplySettlement(entry);
                chat.EnqueueParty($"{p.DisplayName} {GetHandLabel(p, h)}: {h.Bet:N0} gil bet, {h.CardText} vs dealer {session.Round.DealerHand.CardText} → {entry.Outcome}. Return: {entry.TotalReturn:N0} gil. Bank: {p.Bank.Available:N0} gil.");
                log.Add(LogCategory.RoundFlow, $"Settled {p.DisplayName} {GetHandLabel(p, h)}: outcome {entry.Outcome}, wager {entry.Bet:N0}, return {entry.TotalReturn:N0}, player delta {entry.PlayerDelta:N0}.");
            }

            // Zero from a lost hand means the player ran out of betting money.
            // Only a completed outgoing gil trade is a true cash-out.
            if (p.Bank.TotalTracked == 0 && p.Status == PlayerStatus.Playing)
                p.Status = PlayerStatus.SittingOut;
        }
        session.Round.RoundNumber++;
        session.Round.Phase = BlackjackPhase.CashOutBetweenHands;
        pendingInitialDealCards = 0;
        dealerTurnStarted = false;
        allBustRoundOverAnnounced = false;
        lastAstrologianBeneficTurnKey = null;
        nextAstrologianBattleModeRefreshTime = 0;
        foreach (var p in session.SessionPlayers)
        {
            p.BetConfirmed = false;
            p.Hands.Clear();
        }
    }

    private TableSnapshot CaptureSnapshot()
        => new()
        {
            RoundNumber = session.Round.RoundNumber,
            Phase = session.Round.Phase,
            DealerHand = CloneHand(session.Round.DealerHand),
            ActivePlayerIndex = session.Round.ActivePlayerIndex,
            ActiveHandIndex = session.Round.ActiveHandIndex,
            RoundPlayerIdentities = session.Round.Players.Select(p => p.Identity).ToList(),
            PlayerSnapshots = session.SessionPlayers.Select(PlayerSnapshot.From).ToList(),
            PendingInitialDealCards = pendingInitialDealCards,
            DealerTurnStarted = dealerTurnStarted,
            AllBustRoundOverAnnounced = allBustRoundOverAnnounced
        };

    private void RestoreSnapshot(TableSnapshot snapshot)
    {
        dice.ClearPendingAndQueued();

        foreach (var playerSnapshot in snapshot.PlayerSnapshots)
        {
            var player = session.SessionPlayers.FirstOrDefault(p => p.Identity.Equals(playerSnapshot.Identity));
            if (player is null)
            {
                player = new PlayerSessionState { Identity = playerSnapshot.Identity };
                session.SessionPlayers.Add(player);
            }

            playerSnapshot.ApplyTo(player);
        }

        session.SessionPlayers.RemoveAll(p => snapshot.PlayerSnapshots.All(s => !s.Identity.Equals(p.Identity)));

        session.Round.RoundNumber = snapshot.RoundNumber;
        session.Round.Phase = snapshot.Phase;
        session.Round.DealerHand = CloneHand(snapshot.DealerHand);
        session.Round.ActivePlayerIndex = snapshot.ActivePlayerIndex;
        session.Round.ActiveHandIndex = snapshot.ActiveHandIndex;
        session.Round.Players = snapshot.RoundPlayerIdentities
            .Select(id => session.SessionPlayers.FirstOrDefault(p => p.Identity.Equals(id)))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        pendingInitialDealCards = snapshot.PendingInitialDealCards;
        dealerTurnStarted = snapshot.DealerTurnStarted;
        allBustRoundOverAnnounced = snapshot.AllBustRoundOverAnnounced;
        lastAstrologianBeneficTurnKey = null;
        nextAstrologianBattleModeRefreshTime = 0;

        log.Add(LogCategory.Undo, "Blackjack table state restored to the previous action.");
    }

    private sealed class TableSnapshot
    {
        public int RoundNumber { get; init; }
        public BlackjackPhase Phase { get; init; }
        public BlackjackHand DealerHand { get; init; } = new() { HandNumber = 0 };
        public int ActivePlayerIndex { get; init; }
        public int ActiveHandIndex { get; init; }
        public List<PlayerIdentity> RoundPlayerIdentities { get; init; } = [];
        public List<PlayerSnapshot> PlayerSnapshots { get; init; } = [];
        public int PendingInitialDealCards { get; init; }
        public bool DealerTurnStarted { get; init; }
        public bool AllBustRoundOverAnnounced { get; init; }
    }

    private sealed class PlayerSnapshot
    {
        public PlayerIdentity Identity { get; init; }
        public int PartySlot { get; init; }
        public PlayerStatus Status { get; init; }
        public long Available { get; init; }
        public long ActiveBet { get; init; }
        public long LastTradeAmount { get; init; }
        public long LastBet { get; init; }
        public bool HasUnpaidBalance { get; init; }
        public long UnpaidBalance { get; init; }
        public bool BetConfirmed { get; init; }
        public List<BlackjackHand> Hands { get; init; } = [];

        public static PlayerSnapshot From(PlayerSessionState player)
            => new()
            {
                Identity = player.Identity,
                PartySlot = player.PartySlot,
                Status = player.Status,
                Available = player.Bank.Available,
                ActiveBet = player.Bank.ActiveBet,
                LastTradeAmount = player.Bank.LastTradeAmount,
                LastBet = player.Bank.LastBet,
                HasUnpaidBalance = player.Bank.HasUnpaidBalance,
                UnpaidBalance = player.Bank.UnpaidBalance,
                BetConfirmed = player.BetConfirmed,
                Hands = player.Hands.Select(TableTab.CloneHand).ToList()
            };

        public void ApplyTo(PlayerSessionState player)
        {
            player.Identity = Identity;
            player.PartySlot = PartySlot;
            player.Status = Status;
            player.Bank.Available = Available;
            player.Bank.ActiveBet = ActiveBet;
            player.Bank.LastTradeAmount = LastTradeAmount;
            player.Bank.LastBet = LastBet;
            player.Bank.HasUnpaidBalance = HasUnpaidBalance;
            player.Bank.UnpaidBalance = UnpaidBalance;
            player.BetConfirmed = BetConfirmed;
            player.Hands.Clear();
            player.Hands.AddRange(Hands.Select(TableTab.CloneHand));
        }
    }

    private static BlackjackHand CloneHand(BlackjackHand source) => new()
    {
        HandNumber = source.HandNumber,
        Cards = source.Cards.ToList(),
        Bet = source.Bet,
        OriginalBet = source.OriginalBet,
        IsComplete = source.IsComplete,
        IsBusted = source.IsBusted,
        IsDoubled = source.IsDoubled,
        IsSplitHand = source.IsSplitHand,
        IsVoided = source.IsVoided,
        Actions = source.Actions.ToList()
    };
}
