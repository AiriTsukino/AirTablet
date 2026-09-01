using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PrizeTrader;

internal sealed unsafe class TradeSequenceService : IDisposable
{
    public const long MaximumChunk = 1_000_000;
    private static readonly Regex OutgoingGil = new(@"\b(?:you\s+)?(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeComplete = new(@"\btrade\s+complete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeFailed = new(@"\b(?:trade\s+(?:cancelled|canceled|declined|failed)|unable\s+to\s+complete\s+(?:the\s+)?trade|could\s+not\s+complete\s+(?:the\s+)?trade|other\s+player\s+is\s+busy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnavailableToast = new(@"\bis\s+unable\s+to\s+trade\s+at\s+this\s+time\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<bool> autoAcceptIncomingTrades;
    private string lockedName = string.Empty;
    private string lockedWorld = string.Empty;
    private DateTimeOffset nextActionUtc;
    private SequenceStage stage;
    private bool openCommandSent;
    private bool numericValueSet;
    private bool numericSubmitted;
    private bool tradeButtonClicked;
    private long stagedOutgoingAmount;
    private IncomingTradeStage incomingStage;
    private DateTimeOffset nextIncomingActionUtc;
    private string? notification;

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
    public string Status { get; private set; } = "Lock a visible player target to prepare a payout.";

    public TradeSequenceService(Func<bool> autoAcceptIncomingTrades)
    {
        this.autoAcceptIncomingTrades = autoAcceptIncomingTrades;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
        DalamudServices.ToastGui.ErrorToast += OnErrorToast;
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
        DalamudServices.Log.Information("PrizeTrader sequence started. Total={Total:N0}; chunk={Chunk:N0}.", TotalAmount, CurrentChunk);
    }

    public void Cancel(string reason)
    {
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
                DalamudServices.Log.Information("PrizeTrader sent the trade request for chunk {Chunk:N0}.", CurrentChunk);
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
                        DalamudServices.Log.Information("PrizeTrader submitted the numeric gil callback once for {Chunk:N0}.", CurrentChunk);
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
                DalamudServices.Log.Information("PrizeTrader sent the Trade callback once for chunk {Chunk:N0}.", CurrentChunk);
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
                if (!TryConfirmYes(confirmation))
                {
                    Status = "The final Yes button is visible but is not ready yet.";
                    nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                    return;
                }
                stage = SequenceStage.AwaitingCompletion;
                DalamudServices.Log.Information("PrizeTrader sent Yes once and is now passively waiting for trusted completion messages.");
                Status = $"Confirmed Yes once. Waiting for {LockedDisplay} and the trusted Trade complete system message; this wait has no time limit.";
                nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                return;
            }
            Status = $"Waiting for the game's final trade confirmation with {LockedDisplay}; this wait has no time limit.";
            nextActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
            return;
        }

        if (stage == SequenceStage.AwaitingCompletion)
        {
            Status = $"Waiting for the trusted Trade complete system message for the {CurrentChunk:N0} gil trade; this wait has no time limit.";
            nextActionUtc = DateTimeOffset.UtcNow.AddSeconds(1);
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
        if (autoAcceptIncomingTrades()) return;
        ResetIncomingTrade();
    }

    private void TickIncomingTrade()
    {
        if (!autoAcceptIncomingTrades())
        {
            ResetIncomingTrade();
            return;
        }
        if (DateTimeOffset.UtcNow < nextIncomingActionUtc) return;

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
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                Status = "Accepted an incoming trade request once; waiting for the Trade window.";
                DalamudServices.Log.Information("PrizeTrader auto-accepted an incoming trade request once.");
                return;
            }

            // Adopt an already-open Trade window. This covers game/client builds
            // whose incoming request wording is not exposed through PromptText,
            // and a request that was accepted before PrizeTrader observed it.
            if (TryGetReadyAddon("Trade") is null) return;
            incomingStage = IncomingTradeStage.ReadyingEmptySide;
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
            Status = "Detected an open incoming Trade window; preparing to ready the empty PrizeTrader side.";
            DalamudServices.Log.Information("PrizeTrader adopted an already-open incoming Trade window.");
            return;
        }

        if (incomingStage == IncomingTradeStage.WaitingForTradeWindow)
        {
            var trade = TryGetReadyAddon("Trade");
            if (trade is null)
            {
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
                ResetIncomingTrade("The incoming Trade window closed before completion.");
                return;
            }
            if (!IsOtherPartyReady(trade))
            {
                Status = "Waiting for the other player's native Trade ready state before accepting on your side.";
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
                return;
            }
            DalamudServices.Log.Information("PrizeTrader detected the other player's native Trade ready state.");
            if (!TryFireTradeCallback(trade, 0))
            {
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                return;
            }
            incomingStage = IncomingTradeStage.WaitingForFinalPrompt;
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
            Status = "Readied the empty side once; waiting for the final Complete trade prompt.";
            DalamudServices.Log.Information("PrizeTrader readied its empty incoming trade side once.");
            return;
        }

        if (incomingStage == IncomingTradeStage.WaitingForFinalPrompt)
        {
            var confirmation = TryGetReadyAddon("SelectYesno");
            if (confirmation is not null && IsCompleteTradePrompt(confirmation))
            {
                if (!TryConfirmYes(confirmation))
                {
                    nextIncomingActionUtc = DateTimeOffset.UtcNow.AddMilliseconds(350);
                    return;
                }
                incomingStage = IncomingTradeStage.WaitingForCompletion;
                nextIncomingActionUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                Status = "Confirmed the incoming trade Yes once; waiting for the trusted Trade complete system message with no time limit.";
                DalamudServices.Log.Information("PrizeTrader confirmed incoming trade Yes once and is passively waiting for Trade complete.");
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
            Status = "Waiting for the trusted Trade complete system message for the incoming trade; this wait has no time limit.";
            nextIncomingActionUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!IsRunning && incomingStage == IncomingTradeStage.Idle) return;
        var sender = message.OriginalSender.ToString();
        var body = message.OriginalMessage.ToString();
        if (!IsTrustedSystemLine(message, sender)) return;
        var combined = Clean($"{sender} {body}");

        if (!IsRunning)
        {
            if (TradeFailed.IsMatch(combined))
            {
                ResetIncomingTrade("The game reported that the incoming trade did not complete.");
                Notify("PrizeTrader incoming trade did not complete.");
                return;
            }
            if (!TradeComplete.IsMatch(combined)) return;
            DalamudServices.Log.Information("PrizeTrader observed the trusted Trade complete system line for the incoming trade.");
            ResetIncomingTrade("Incoming trade complete.");
            Notify("PrizeTrader incoming trade completed.");
            return;
        }

        if (TradeFailed.IsMatch(combined))
        {
            PauseFailedTrade("The game reported that the trade did not complete.");
            return;
        }
        var outgoing = OutgoingGil.Match(combined);
        if (outgoing.Success && TryParseAmount(outgoing.Groups["amount"].Value, out var amount))
        {
            if (amount == CurrentChunk)
            {
                stagedOutgoingAmount = amount;
                DalamudServices.Log.Information("PrizeTrader observed the matching trusted outgoing gil line for {Amount:N0}.", amount);
                Status = $"The game reported {amount:N0} gil handed over; waiting for Trade complete before counting it.";
            }
            else
            {
                Cancel($"The game reported {amount:N0} gil, which does not match the expected {CurrentChunk:N0} gil chunk.");
            }
            return;
        }
        if (!TradeComplete.IsMatch(combined)) return;
        DalamudServices.Log.Information("PrizeTrader observed the trusted Trade complete system line.");
        if (stagedOutgoingAmount != CurrentChunk || stagedOutgoingAmount <= 0)
        {
            Cancel("A Trade complete message arrived without the exact expected outgoing gil confirmation.");
            return;
        }
        ConfirmedAmount += stagedOutgoingAmount;
        if (RemainingAmount <= 0)
        {
            CompletePayout();
            return;
        }
        var completed = stagedOutgoingAmount;
        ResetChunkState();
        stage = SequenceStage.OpeningTrade;
        nextActionUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        Status = $"Confirmed {completed:N0} gil. Preparing the next {CurrentChunk:N0} gil trade; {RemainingAmount:N0} gil remains.";
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (!UnavailableToast.IsMatch(Clean(message.TextValue))) return;
        if (IsRunning)
            PauseFailedTrade(message.TextValue.Trim());
        else if (incomingStage != IncomingTradeStage.Idle)
            ResetIncomingTrade(message.TextValue.Trim());
    }

    private void PauseFailedTrade(string reason)
    {
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
        stagedOutgoingAmount = 0;
        nextActionUtc = DateTimeOffset.MinValue;
    }

    private void ResetIncomingTrade(string? status = null)
    {
        incomingStage = IncomingTradeStage.Idle;
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

    private static bool IsTrustedSystemLine(IHandleableChatMessage message, string sender)
    {
        var kind = string.Empty;
        try { kind = message.LogKind.ToString(); } catch { }
        if (kind.Contains("Say", StringComparison.OrdinalIgnoreCase) || kind.Contains("Tell", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("Party", StringComparison.OrdinalIgnoreCase) || kind.Contains("Shout", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("Yell", StringComparison.OrdinalIgnoreCase) || kind.Contains("Linkshell", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(Clean(sender)) || kind.Contains("System", StringComparison.OrdinalIgnoreCase) || kind.Contains("Log", StringComparison.OrdinalIgnoreCase) || kind.Contains("Notice", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SendGameCommand(string command)
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
    private static bool TryParseAmount(string value, out long amount) => long.TryParse(value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) && amount > 0;
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
