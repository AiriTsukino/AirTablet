using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PrizeTrader;

internal sealed unsafe class TradeSequenceService : IDisposable
{
    private const int MaximumDiagnosticEntries = 1000;
    public const long MaximumChunk = 1_000_000;
    private static readonly Regex UnavailableToast = new(@"\bis\s+unable\s+to\s+trade\s+at\s+this\s+time\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<bool> autoAcceptIncomingTrades;
    private readonly object diagnosticLock = new();
    private readonly List<string> diagnosticEntries = [];
    private string lockedName = string.Empty;
    private string lockedWorld = string.Empty;
    private DateTimeOffset nextActionUtc;
    private SequenceStage stage;
    private bool openCommandSent;
    private bool numericValueSet;
    private bool numericSubmitted;
    private bool tradeButtonClicked;
    private readonly TradeCompletionTracker completion = new();
    private ulong tradeCharacterId;
    private uint tradeTerritoryId;
    private IncomingTradeStage incomingStage;
    private readonly IncomingTradeWindowTracker incomingWindow = new();
    private DateTimeOffset incomingRequestAcceptedAt;
    private DateTimeOffset nextIncomingActionUtc;
    private string? notification;
    private string status = "Lock a visible player target to prepare a payout.";

    public string? LockedDisplay => HasLockedTarget ? $"{lockedName}@{lockedWorld}" : null;
    public bool HasLockedTarget => !string.IsNullOrWhiteSpace(lockedName) && !string.IsNullOrWhiteSpace(lockedWorld);
    public bool IsRunning => stage != SequenceStage.Idle;
    public bool IsIncomingTradeActive => incomingStage != IncomingTradeStage.Idle;
    public bool IsBusy => IsRunning || IsIncomingTradeActive;
    public bool NeedsRetry => stage == SequenceStage.PausedAfterFailure;
    public long TotalAmount { get; private set; }
    public long ConfirmedAmount { get; private set; }
    public long RemainingAmount => Math.Max(0, TotalAmount - ConfirmedAmount);
    public long CurrentChunk => IsRunning ? Math.Min(MaximumChunk, RemainingAmount) : 0;
    public string Status
    {
        get => status;
        private set
        {
            if (status.Equals(value, StringComparison.Ordinal)) return;
            status = value;
            Trace($"Status: {value}");
        }
    }
    public string DebugLogText
    {
        get
        {
            lock (diagnosticLock)
                return string.Join(Environment.NewLine, diagnosticEntries);
        }
    }
    public int DebugLogLineCount
    {
        get
        {
            lock (diagnosticLock)
                return diagnosticEntries.Count;
        }
    }

    public TradeSequenceService(Func<bool> autoAcceptIncomingTrades)
    {
        this.autoAcceptIncomingTrades = autoAcceptIncomingTrades;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
        DalamudServices.ToastGui.ErrorToast += OnErrorToast;
        Trace("PrizeTrader diagnostics started.");
        Trace($"Automatic incoming trade acceptance is {(autoAcceptIncomingTrades() ? "enabled" : "disabled")}.");
        Trace($"Status: {Status}");
    }

    public void ClearDebugLog()
    {
        lock (diagnosticLock)
            diagnosticEntries.Clear();
    }

    public void LockCurrentTarget()
    {
        if (IsBusy) return;
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter player || !TryIdentity(player, out var name, out var world))
        {
            lockedName = string.Empty;
            lockedWorld = string.Empty;
            ClearDisplayedProgress("No target is locked. Previous trade progress was cleared.");
            Notify("Target a visible player before locking a PrizeTrader recipient.");
            Status = "No visible player target could be locked.";
            return;
        }
        var changed = !name.Equals(lockedName, StringComparison.OrdinalIgnoreCase)
            || !world.Equals(lockedWorld, StringComparison.OrdinalIgnoreCase);
        if (changed)
            ClearDisplayedProgress("The locked recipient changed. Previous trade progress was cleared.");
        lockedName = name;
        lockedWorld = world;
        Status = $"Locked {LockedDisplay}. Enter the total payout amount.";
        Notify($"PrizeTrader locked {LockedDisplay}.");
    }

    public void Start(long total)
    {
        if (!HasLockedTarget || IsBusy || total <= 0) return;
        TotalAmount = total;
        ConfirmedAmount = 0;
        ResetChunkState();
        stage = SequenceStage.OpeningTrade;
        Status = $"Preparing the first {CurrentChunk:N0} gil trade with {LockedDisplay}.";
        Trace($"Sequence started. Total={TotalAmount:N0}; chunk={CurrentChunk:N0}; recipient={LockedDisplay}.");
    }

    public void Cancel(string reason)
    {
        // Preserve a paid chunk even if Cancel is pressed before the next tick.
        TryRecordCompletedChunk(advance: false);
        if (completion.ReportedAmount > 0)
            reason += $" The game reported {completion.ReportedAmount:N0} gil handed over, but this payment could not be verified. Check the recipient's balance before paying again.";
        else if (completion.ObservedDecrease is > 0)
            reason += $" The operator's gil decreased by {completion.ObservedDecrease:N0}, but this payment could not be verified. Check the recipient's balance before paying again.";
        stage = SequenceStage.Idle;
        ResetChunkState();
        Status = $"Payout stopped. {ConfirmedAmount:N0} of {TotalAmount:N0} gil was confirmed. {reason}";
        Notify("PrizeTrader payout cancelled.");
    }

    public void RetryCurrentTrade()
    {
        if (!NeedsRetry) return;
        ResetChunkState();
        stage = SequenceStage.OpeningTrade;
        Status = $"Retrying the unchanged {CurrentChunk:N0} gil chunk with {LockedDisplay}.";
    }

    public void Tick()
    {
        if (!IsRunning)
        {
            TickIncomingTrade();
            return;
        }
        if (NeedsRetry || DateTimeOffset.UtcNow < nextActionUtc) return;
        if (stage == SequenceStage.AwaitingCompletion && !IsSameTradeContext())
        {
            Cancel("The operator logged out, changed character, or changed zones during the trade. Verify the payment before restarting.");
            return;
        }
        if (RemainingAmount <= 0)
        {
            CompletePayout();
            return;
        }

        if (stage != SequenceStage.AwaitingCompletion && !TryFindLockedPlayer(out _))
        {
            Status = $"Waiting for {LockedDisplay} to be visible in the current zone. No trade will be opened until the exact locked player can be seen.";
            nextActionUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            return;
        }

        if (stage == SequenceStage.OpeningTrade)
        {
            TryFindLockedPlayer(out var player);
            if (TryGetReadyAddon("Trade") is not null)
            {
                stage = SequenceStage.EnteringGil;
                Status = $"Trade opened with {LockedDisplay}; entering {CurrentChunk:N0} gil.";
                nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                return;
            }
            if (!openCommandSent)
            {
                DalamudServices.TargetManager.Target = player;
                if (!SendGameCommand("/trade <t>"))
                {
                    Status = "The game trade command could not be sent. The payout remains paused.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddSeconds(2);
                    return;
                }
                openCommandSent = true;
                Trace($"Sent the trade request for chunk {CurrentChunk:N0}.");
                Status = $"Trade request sent to {LockedDisplay}; waiting for the normal Trade window. There is no response timeout.";
            }
            nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
            return;
        }

        if (stage == SequenceStage.EnteringGil)
        {
            var trade = TryGetReadyAddon("Trade");
            if (trade is null)
            {
                PauseFailedTrade("The Trade window closed before this chunk completed.");
                return;
            }
            var numericBase = TryGetReadyAddon("InputNumeric");
            if (numericBase is not null)
            {
                var numeric = (AddonInputNumeric*)numericBase;
                if (!numericValueSet)
                {
                    if (TrySetNumericInput(numeric, checked((int)CurrentChunk)))
                    {
                        numericValueSet = true;
                        Status = $"Entered {CurrentChunk:N0} gil; confirming the amount.";
                    }
                    else Status = "The gil entry is open, but its numeric field is not ready yet.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
                    return;
                }
                if (!numericSubmitted)
                {
                    if (TrySubmitNumericInput(numeric, checked((int)CurrentChunk)))
                    {
                        numericSubmitted = true;
                        Trace($"Submitted the numeric gil callback once for {CurrentChunk:N0}.");
                        Status = $"Confirmed the {CurrentChunk:N0} gil entry; preparing the trade.";
                    }
                    else Status = "The gil amount is entered, but the confirmation control is not ready yet.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                }
                return;
            }
            if (!numericSubmitted)
            {
                Status = TryClickGilControl(trade)
                    ? $"Opening the gil entry for {CurrentChunk:N0}."
                    : "The Trade window is open, but its Gil control is not ready yet.";
                nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                return;
            }
            if (!tradeButtonClicked)
            {
                if (!TryFireTradeCallback(trade, 0))
                {
                    Status = "The exact Trade confirmation button is not ready yet.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                    return;
                }
                tradeButtonClicked = true;
                stage = SequenceStage.ConfirmingTrade;
                Trace($"Sent the Trade callback once for chunk {CurrentChunk:N0}.");
                nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                Status = $"Submitted {CurrentChunk:N0} gil; waiting for the game's final trade confirmation.";
            }
            return;
        }

        if (stage == SequenceStage.ConfirmingTrade)
        {
            var confirmation = TryGetReadyAddon("SelectYesno");
            if (confirmation is not null)
            {
                if (!IsCompleteTradePrompt(confirmation))
                {
                    Status = "Another Yes/No prompt is open. PrizeTrader will only confirm the game's Complete trade prompt.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                    return;
                }
                // Arm before the callback so synchronous chat events belong to
                // this chunk, not the next one.
                tradeCharacterId = DalamudServices.PlayerState.ContentId;
                tradeTerritoryId = DalamudServices.ClientState.TerritoryType;
                var startingGil = ReadOperatorGil();
                completion.Begin(CurrentChunk, startingGil);
                Trace(startingGil is not null
                    ? $"Captured operator gil before final confirmation: {startingGil:N0}; expected decrease={CurrentChunk:N0}."
                    : "Operator gil balance is unavailable; this chunk requires the exact handover system message.");
                stage = SequenceStage.AwaitingCompletion;
                if (!TryConfirmYes(confirmation))
                {
                    completion.Reset();
                    stage = SequenceStage.ConfirmingTrade;
                    Status = "The final Yes button is visible but is not ready yet.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                    return;
                }
                if (stage != SequenceStage.AwaitingCompletion) return;
                Trace("Sent Yes once and began waiting for exact payment evidence and Trade window closure.");
                Status = $"Confirmed Yes once. Waiting for {LockedDisplay} and the exact gil handover or balance decrease, followed by the Trade window closing.";
                nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(100);
                return;
            }
            Status = $"Waiting for the game's final trade confirmation with {LockedDisplay}; this wait has no time limit.";
            nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
            return;
        }

        if (stage == SequenceStage.AwaitingCompletion)
        {
            if (TryRecordCompletedChunk()) return;
            if (completion.HasUnexpectedBalanceChange)
            {
                Cancel($"The operator's gil changed unexpectedly (decrease={completion.ObservedDecrease:N0}; expected={CurrentChunk:N0}). Automatic payout stopped for review.");
                return;
            }
            Status = completion.ReportedAmount > 0
                ? $"The game reported {completion.ReportedAmount:N0} gil handed over; waiting for the Trade window to close."
                : completion.BalanceCheckExpired
                    ? $"No exact gil decrease was observed within 3 seconds of the final confirmation closing. Waiting for the exact {CurrentChunk:N0} gil handover message; verify payment before restarting."
                    : $"Waiting for an exact {CurrentChunk:N0} gil handover or operator balance decrease and a closed Trade window; closure alone is not payment.";
            nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(100);
        }
    }

    public void ClearDisplayedProgress(string reason)
    {
        if (IsBusy) return;
        TotalAmount = 0;
        ConfirmedAmount = 0;
        ResetChunkState();
        Status = reason;
    }

    public void OnIncomingAutoAcceptSettingChanged()
    {
        Trace($"Automatic incoming trade acceptance changed to {(autoAcceptIncomingTrades() ? "enabled" : "disabled")}.");
        if (autoAcceptIncomingTrades()) return;
        ResetIncomingTrade();
    }

    private void TickIncomingTrade()
    {
        if (!DalamudServices.ClientState.IsLoggedIn)
        {
            ResetIncomingTrade();
            incomingWindow.Reset();
            return;
        }
        if (!autoAcceptIncomingTrades())
        {
            ResetIncomingTrade();
            return;
        }
        if (DateTimeOffset.UtcNow < nextIncomingActionUtc) return;
        var windowVisible = ReadTradeWindowVisibility();
        var windowClosed = incomingWindow.Observe(windowVisible);
        if (windowClosed && incomingStage != IncomingTradeStage.Idle)
        {
            Trace($"Observed incoming Trade window close in {incomingStage}; released incoming state without relying on system chat. No payout or bank amount was recorded.");
            ResetIncomingTrade("Incoming Trade window closed. Ready for the next trade; no payment is inferred from closure alone.");
            return;
        }
        if (incomingWindow.AwaitingClosure) return;

        if (incomingStage == IncomingTradeStage.Idle)
        {
            var request = TryGetReadyAddon("SelectYesno");
            if (request is not null && IsIncomingTradeRequestPrompt(request))
            {
                if (!TryConfirmYes(request))
                {
                    nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                    return;
                }
                incomingStage = IncomingTradeStage.WaitingForTradeWindow;
                incomingRequestAcceptedAt = DateTimeOffset.UtcNow;
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                Status = "Accepted an incoming trade request once; waiting for the Trade window.";
                Trace("Auto-accepted an incoming trade request once.");
                return;
            }

            // Adopt an already-open Trade window. This covers game/client builds
            // whose incoming request wording is not exposed through PromptText,
            // and a request that was accepted before PrizeTrader observed it.
            if (TryGetReadyAddon("Trade") is null) return;
            incomingStage = IncomingTradeStage.ReadyingEmptySide;
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
            Status = "Detected an open incoming Trade window; preparing to ready the empty PrizeTrader side.";
            Trace("Adopted an already-open incoming Trade window.");
            return;
        }

        if (incomingStage == IncomingTradeStage.WaitingForTradeWindow)
        {
            var trade = TryGetReadyAddon("Trade");
            if (trade is null)
            {
                if (windowVisible == false && DateTimeOffset.UtcNow - incomingRequestAcceptedAt > TimeSpan.FromSeconds(10))
                {
                    Trace("Accepted request did not open a Trade window within 10 seconds; cleared the incoming request state without recording payment.");
                    ResetIncomingTrade("No Trade window opened for the accepted request. Ready for the next trade.");
                    return;
                }
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                return;
            }
            incomingStage = IncomingTradeStage.ReadyingEmptySide;
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
            Status = "Incoming Trade window opened; preparing to ready the empty PrizeTrader side.";
            return;
        }

        if (incomingStage == IncomingTradeStage.ReadyingEmptySide)
        {
            var trade = TryGetReadyAddon("Trade");
            if (trade is null)
            {
                // A visible but not-ready addon is not a closed trade.
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(100);
                return;
            }
            if (!IsOtherPartyReady(trade))
            {
                Status = "Waiting for the other player's native Trade ready state before accepting on your side.";
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
                return;
            }
            Trace("Detected the other player's native Trade ready state.");
            if (!TryFireTradeCallback(trade, 0))
            {
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                return;
            }
            incomingStage = IncomingTradeStage.WaitingForFinalPrompt;
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
            Status = "Readied the empty side once; waiting for the final Complete trade prompt.";
            Trace("Readied the empty incoming trade side once.");
            return;
        }

        if (incomingStage == IncomingTradeStage.WaitingForFinalPrompt)
        {
            var confirmation = TryGetReadyAddon("SelectYesno");
            if (confirmation is not null && IsCompleteTradePrompt(confirmation))
            {
                incomingStage = IncomingTradeStage.WaitingForCompletion;
                if (!TryConfirmYes(confirmation))
                {
                    if (incomingStage != IncomingTradeStage.WaitingForCompletion) return;
                    incomingStage = IncomingTradeStage.WaitingForFinalPrompt;
                    nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                    return;
                }
                if (incomingStage != IncomingTradeStage.WaitingForCompletion) return;
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(100);
                Status = "Confirmed the incoming trade Yes once; waiting for the Trade window to close or a trusted completion message.";
                Trace("Confirmed incoming trade Yes once; monitoring native window closure independently of system messages.");
                return;
            }

            var trade = TryGetReadyAddon("Trade");
            if (trade is not null && !IsOtherPartyReady(trade))
            {
                incomingStage = IncomingTradeStage.ReadyingEmptySide;
                Status = "The other player changed their offer. Waiting for their new green OK before accepting again.";
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
                return;
            }
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
            return;
        }

        if (incomingStage == IncomingTradeStage.WaitingForCompletion)
        {
            Status = "Waiting for the incoming Trade window to close. System messages are optional; the other player can take as long as needed.";
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(100);
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!IsRunning && incomingStage == IncomingTradeStage.Idle) return;
        var sender = Clean(message.OriginalSender.ExtractText());
        var body = Clean(message.OriginalMessage.ExtractText());
        var kind = DescribeChatKind(message);
        var evidence = TradeChatEvidence.Classify(kind, sender, body, out var amount);
        if (evidence == TradeMessageKind.None) return;
        Trace($"Observed trusted trade line: kind={kind}; text={body}");

        if (!IsRunning)
        {
            if (evidence == TradeMessageKind.Failed)
            {
                ResetIncomingTrade("The game reported that the incoming trade did not complete.");
                Notify("PrizeTrader incoming trade did not complete.");
                return;
            }
            // A late completion from the previous trade must not reset a new
            // incoming request that has not reached final confirmation yet.
            if (evidence != TradeMessageKind.Complete || incomingStage != IncomingTradeStage.WaitingForCompletion) return;
            Trace("Matched the trusted Trade complete system line for the incoming trade.");
            ResetIncomingTrade("Incoming trade complete.");
            Notify("PrizeTrader incoming trade completed.");
            return;
        }

        if (evidence == TradeMessageKind.Failed)
        {
            PauseFailedTrade("The game reported that the trade did not complete.");
            return;
        }
        // Late/duplicate completion lines while opening or retrying a trade
        // cannot be credited to a new chunk that has not been confirmed yet.
        if (stage != SequenceStage.AwaitingCompletion) return;
        if (evidence == TradeMessageKind.Handover)
        {
            if (completion.ObserveHandover(amount))
            {
                Trace($"Matched the trusted outgoing gil line for {amount:N0}.");
                Status = $"The game reported {amount:N0} gil handed over; waiting for the Trade window to close before counting it.";
            }
            else
            {
                Cancel($"The game reported {amount:N0} gil, which does not match the expected {CurrentChunk:N0} gil chunk.");
            }
            return;
        }
        if (evidence != TradeMessageKind.Complete) return;
        Trace("Matched the trusted Trade complete system line.");
        completion.ObserveCompletion();
    }

    private bool TryRecordCompletedChunk(bool advance = true)
    {
        if (stage != SequenceStage.AwaitingCompletion) return false;
        var visible = IsSameTradeContext() ? ReadTradeWindowVisibility() : null;
        ObserveOperatorBalance();
        var sawCompletionMessage = completion.SawCompletionMessage;
        var sawHandover = completion.ReportedAmount > 0;
        var sawBalanceDecrease = completion.HasBalanceConfirmation;
        var observedDecrease = completion.ObservedDecrease;
        if (!completion.TryConsume(visible, out var completed)) return false;
        ConfirmedAmount += completed;
        Trace($"Confirmed {completed:N0} gil once with a closed Trade window. Exact handover message={sawHandover}; exact operator gil decrease={sawBalanceDecrease}; observed decrease={observedDecrease?.ToString("N0") ?? "unavailable"}; separate Trade complete message={sawCompletionMessage}.");
        if (!advance) return true;
        if (RemainingAmount <= 0)
        {
            CompletePayout();
            return true;
        }
        ResetChunkState();
        stage = SequenceStage.OpeningTrade;
        nextActionUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        Status = $"Confirmed {completed:N0} gil. Preparing the next {CurrentChunk:N0} gil trade; {RemainingAmount:N0} gil remains.";
        return true;
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (!UnavailableToast.IsMatch(Clean(message.TextValue))) return;
        Trace($"Observed matching trade error toast: {Clean(message.TextValue)}");
        if (IsRunning)
            PauseFailedTrade(message.TextValue.Trim());
        else if (incomingStage != IncomingTradeStage.Idle)
            ResetIncomingTrade(message.TextValue.Trim());
    }

    private void PauseFailedTrade(string reason)
    {
        if (stage == SequenceStage.AwaitingCompletion)
        {
            ObserveOperatorBalance();
            completion.ObserveFailure();
        }
        if (completion.ReportedAmount > 0 || completion.ObservedDecrease is > 0)
        {
            // Conflicting payment/failure evidence must never offer a blind
            // retry of a chunk the game already reported handing over.
            Cancel(reason);
            return;
        }
        ResetChunkState();
        stage = SequenceStage.PausedAfterFailure;
        Status = $"{reason} Nothing was counted. Review the situation, then retry the unchanged chunk or cancel.";
        Notify("PrizeTrader trade did not complete; the payout amount was not advanced.");
    }

    private void CompletePayout()
    {
        stage = SequenceStage.Idle;
        ResetChunkState();
        Status = $"Payout complete: {ConfirmedAmount:N0} gil was confirmed to {LockedDisplay}.";
        Notify($"PrizeTrader completed {ConfirmedAmount:N0} gil to {LockedDisplay}.");
    }

    private void ResetChunkState()
    {
        openCommandSent = false;
        numericValueSet = false;
        numericSubmitted = false;
        tradeButtonClicked = false;
        completion.Reset();
        tradeCharacterId = 0;
        tradeTerritoryId = 0;
        nextActionUtc = DateTimeOffset.MinValue;
    }

    private void ResetIncomingTrade(string? status = null)
    {
        if (incomingStage != IncomingTradeStage.Idle) incomingWindow.Finish(ReadTradeWindowVisibility());
        incomingStage = IncomingTradeStage.Idle;
        incomingRequestAcceptedAt = default;
        nextIncomingActionUtc = DateTimeOffset.MinValue;
        if (!string.IsNullOrWhiteSpace(status)) Status = status;
    }

    private bool TryFindLockedPlayer(out IPlayerCharacter player)
    {
        player = null!;
        var matches = new List<IPlayerCharacter>();
        foreach (var battleCharacter in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (battleCharacter is not IPlayerCharacter candidate) continue;
            if (!TryIdentity(candidate, out var name, out var world) ||
                !name.Equals(lockedName, StringComparison.OrdinalIgnoreCase) ||
                !world.Equals(lockedWorld, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(candidate);
        }
        if (matches.Count != 1) return false;
        player = matches[0];
        return true;
    }

    private static bool TryIdentity(IPlayerCharacter player, out string name, out string world)
    {
        name = player.Name.ToString().Trim();
        world = string.Empty;
        try { world = player.HomeWorld.Value.Name.ToString().Trim(); } catch { }
        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world);
    }

    private bool SendGameCommand(string command)
    {
        try
        {
            using var value = new Utf8String(command);
            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell is null || ui is null) return false;
            shell->ExecuteCommandInner(&value, ui);
            return true;
        }
        catch (Exception ex)
        {
            Trace($"ERROR sending the trade command: {ex.GetType().Name}: {ex.Message}");
            DalamudServices.Log.Warning(ex, "PrizeTrader could not send the trade command.");
            return false;
        }
    }

    private static AtkUnitBase* TryGetReadyAddon(string name)
    {
        try
        {
            var ptr = DalamudServices.GameGui.GetAddonByName(name);
            if (ptr.Address == nint.Zero) return null;
            var addon = (AtkUnitBase*)ptr.Address;
            return addon is not null && addon->IsReady && addon->IsVisible ? addon : null;
        }
        catch { return null; }
    }

    private static bool? ReadTradeWindowVisibility()
    {
        try
        {
            if (!DalamudServices.ClientState.IsLoggedIn) return null;
            var ptr = DalamudServices.GameGui.GetAddonByName("Trade");
            if (ptr.Address == nint.Zero) return false;
            // A not-ready but still visible addon is not a closed window.
            return ((AtkUnitBase*)ptr.Address)->IsVisible;
        }
        catch { return null; }
    }

    private bool IsSameTradeContext() => DalamudServices.ClientState.IsLoggedIn
        && tradeCharacterId != 0 && DalamudServices.PlayerState.ContentId == tradeCharacterId
        && DalamudServices.ClientState.TerritoryType == tradeTerritoryId;

    private void ObserveOperatorBalance()
    {
        // Stop native balance reads as soon as the exact amount is detected,
        // or the short post-confirmation verification window expires.
        if (!completion.NeedsBalanceObservation) return;
        completion.ObserveBalance(ReadOperatorGil(), ReadFinalConfirmationVisibility(), DateTimeOffset.UtcNow);
    }

    private bool? ReadFinalConfirmationVisibility()
    {
        try
        {
            if (!IsSameTradeContext()) return null;
            var ptr = DalamudServices.GameGui.GetAddonByName("SelectYesno");
            if (ptr.Address == nint.Zero) return false;
            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible) return false;
            if (!addon->IsReady) return null;
            return IsCompleteTradePrompt(addon);
        }
        catch { return null; }
    }

    private long? ReadOperatorGil()
    {
        try
        {
            if (!IsSameTradeContext()) return null;
            var inventory = InventoryManager.Instance();
            if (inventory is null) return null;
            var currency = inventory->GetInventoryContainer(InventoryType.Currency);
            if (currency is null || !currency->IsLoaded) return null;
            return inventory->GetGil();
        }
        catch { return null; }
    }

    private static bool TrySetNumericInput(AddonInputNumeric* addon, int value)
    {
        if (addon is null) return false;
        var input = FindNumericInput(addon);
        if (input is null) return false;
        var text = value.ToString(CultureInfo.InvariantCulture);
        input->RawString.SetString(text);
        input->EvaluatedString.SetString(text);
        input->SetValue(value);
        input->Value = value;
        input->CursorPos = text.Length;
        input->SelectionStart = input->CursorPos;
        input->SelectionEnd = input->CursorPos;
        input->UpdateTextNode();
        return input->Value == value;
    }

    private static bool TrySubmitNumericInput(AddonInputNumeric* addon, int value)
    {
        if (addon is null || !TrySetNumericInput(addon, value)) return false;
        var values = stackalloc AtkValue[1];
        values[0] = new AtkValue();
        values[0].SetInt(value);
        addon->AtkUnitBase.FireCallback(1, values, true);
        return true;
    }

    private static AtkComponentNumericInput* FindNumericInput(AddonInputNumeric* addon)
    {
        if (addon is null) return null;
        if (addon->NumericInput is not null) return addon->NumericInput;
        var unit = &addon->AtkUnitBase;
        if (unit->UldManager.NodeList is null) return null;
        for (var i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node is null || (uint)node->Type < 1000) continue;
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode is null || componentNode->Component is null
                || componentNode->Component->GetComponentType() != ComponentType.NumericInput) continue;
            return (AtkComponentNumericInput*)componentNode->Component;
        }
        return null;
    }

    private static bool TryFireTradeCallback(AtkUnitBase* addon, int action)
    {
        if (addon is null || !addon->IsReady || !addon->IsVisible) return false;
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue();
        values[0].SetInt(action);
        // Trade expects a deliberately empty second value. Supplying an integer
        // zero here is not equivalent and can leave the addon on the same screen.
        values[1] = new AtkValue();
        addon->FireCallback(2, values, true);
        return true;
    }

    private static bool TryClickGilControl(AtkUnitBase* addon)
    {
        return TryFireTradeCallback(addon, 2);
    }

    private static bool IsCompleteTradePrompt(AtkUnitBase* addon)
    {
        var prompt = ReadYesNoText(addon);
        return prompt.Contains("Complete trade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIncomingTradeRequestPrompt(AtkUnitBase* addon)
    {
        var prompt = ReadYesNoText(addon);
        if (string.IsNullOrWhiteSpace(prompt) || IsCompleteTradePrompt(addon)) return false;
        return prompt.Contains("trade", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadYesNoText(AtkUnitBase* addon)
    {
        if (addon is null) return string.Empty;
        var text = new List<string>();
        var yesNo = (AddonSelectYesno*)addon;
        var prompt = Clean(yesNo->PromptText is null ? string.Empty : yesNo->PromptText->NodeText.ToString());
        if (!string.IsNullOrWhiteSpace(prompt)) text.Add(prompt);
        if (addon->UldManager.NodeList is null) return string.Join(" ", text);
        var count = Math.Min((int)addon->UldManager.NodeListCount, 256);
        for (var i = 0; i < count; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node is null || (uint)node->Type != 3) continue;
            var textNode = node->GetAsAtkTextNode();
            if (textNode is null) continue;
            var value = Clean(textNode->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(value) && !text.Contains(value, StringComparer.OrdinalIgnoreCase))
                text.Add(value);
        }
        return string.Join(" ", text);
    }

    private static bool IsOtherPartyReady(AtkUnitBase* trade)
    {
        if (trade is null || trade->UldManager.NodeList is null) return false;

        // The Trade addon has two independent ready banners. Component node 4
        // is the local player's banner; node 5 is the other player's banner.
        // Inside each component, text node 2 contains "OK" and its rendered
        // alpha changes from 0 to 255 when that side accepts. Checking the
        // partner component specifically avoids color/theme heuristics and
        // cannot mistake our own green check for the sender's acceptance.
        var count = Math.Min((int)trade->UldManager.NodeListCount, 512);
        for (var i = 0; i < count; i++)
        {
            var node = trade->UldManager.NodeList[i];
            if (node is null || node->NodeId != 5 || (uint)node->Type < 1000) continue;
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is null) return false;
            var manager = componentNode->Component->UldManager;
            if (manager.NodeList is null) return false;
            var innerCount = Math.Min((int)manager.NodeListCount, 64);
            for (var j = 0; j < innerCount; j++)
            {
                var inner = manager.NodeList[j];
                if (inner is null || inner->NodeId != 2 || inner->Type != NodeType.Text) continue;
                var text = (AtkTextNode*)inner;
                return Normalize(text->NodeText.ToString()).Equals("ok", StringComparison.OrdinalIgnoreCase)
                    && inner->IsVisible()
                    && inner->Alpha_2 > 0
                    && (inner->DrawFlags & 2) == 0;
            }
        }
        return false;
    }

    private static bool TryConfirmYes(AtkUnitBase* addon)
    {
        if (addon is null || !addon->IsReady || !addon->IsVisible) return false;
        var yesNo = (AddonSelectYesno*)addon;
        var button = yesNo->YesButton;
        if (button is null || !button->IsEnabled) return false;
        // Match the game's proven SelectYesno callback shape exactly: one Int
        // value containing 0 (Yes), with addon state updates enabled. The
        // FireCallbackInt convenience helper does not expose that updateState
        // argument and can dismiss the local dialog without advancing the
        // shared trade confirmation correctly.
        var values = stackalloc AtkValue[1];
        values[0] = new AtkValue();
        values[0].SetInt(0);
        addon->FireCallback(1, values, true);
        return true;
    }

    private static string Clean(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string DescribeChatKind(IHandleableChatMessage message)
    {
        try { return message.LogKind.ToString(); }
        catch { return "unknown"; }
    }
    private void Trace(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {Clean(message)}";
        lock (diagnosticLock)
        {
            diagnosticEntries.Add(line);
            if (diagnosticEntries.Count > MaximumDiagnosticEntries)
                diagnosticEntries.RemoveRange(0, diagnosticEntries.Count - MaximumDiagnosticEntries);
        }
        DalamudServices.Log.Information("[PrizeTrader] {Message}", message);
    }
    private void Notify(string value) => notification = value;
    public string? ConsumeNotification() { var value = notification; notification = null; return value; }

    public void Dispose()
    {
        DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
        DalamudServices.ToastGui.ErrorToast -= OnErrorToast;
    }

    private enum SequenceStage { Idle, OpeningTrade, EnteringGil, ConfirmingTrade, AwaitingCompletion, PausedAfterFailure }
    private enum IncomingTradeStage { Idle, WaitingForTradeWindow, ReadyingEmptySide, WaitingForFinalPrompt, WaitingForCompletion }
}
