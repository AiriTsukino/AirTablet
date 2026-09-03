using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed unsafe class TradeMonitorService
{
    private static readonly TimeSpan DuplicateTradeWindow = TimeSpan.FromSeconds(5);
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly DealerLedgerService ledger;
    private readonly LogService log;
    private readonly HashSet<long> observedTradeGilAmounts = [];
    private readonly Dictionary<string, DateTime> recentAutomaticTrades = new(StringComparer.OrdinalIgnoreCase);
    private DateTime tradeWindowLikelyOpenUntilUtc = DateTime.MinValue;
    private bool trustedTradeSequenceActive;
    private bool tradeAddonSeenDuringSequence;
    private DateTime nextTradeWindowPollAtUtc = DateTime.MinValue;
    private DateTime lastTradeWindowSeenAtUtc = DateTime.MinValue;
    private DateTime lastTradeAmountObservedAtUtc = DateTime.MinValue;
    private string lastObservedTradeAmountSignature = string.Empty;
    private TradeBalanceEvidence? balanceEvidence;
    private PlayerIdentity balancePartner;
    private ulong balanceOperatorId;
    private uint balanceTerritoryId;
    private uint balancePartnerEntityId;
    private bool balanceWindowVisible;
    private DateTime balancePartnerSeenAt = DateTime.MinValue;
    private DateTime nextBalanceWarningAt = DateTime.MinValue;
    public List<TradeEntry> Trades { get; } = [];
    public bool AutomaticDetectionEnabled => config.General.AutomaticTradeDetectionEnabled;
    public bool IsTradeWindowLikelyOpen
        => AutomaticDetectionEnabled
           && (DateTime.UtcNow < tradeWindowLikelyOpenUntilUtc
               || trustedTradeSequenceActive
               || IsTradeAddonOpen());
    public string DetectionStatus => AutomaticDetectionEnabled
        ? "Automatic detection is on. Exact trade gil changes are checked first for up to 3 seconds after final confirmation; trusted system messages remain available. Only confirmed current-party trade partners can adjust banks."
        : "Automatic detection is off. Use manual trade entry to adjust player banks.";

    public TradeMonitorService(Configuration config, BlackjackSession session, DealerLedgerService ledger, LogService log)
    {
        this.config = config;
        this.session = session;
        this.ledger = ledger;
        this.log = log;
    }

    public TradeEntry AddManualTrade(PlayerIdentity from, PlayerIdentity to, long amount, TradeClassification classification, string note = "Manual entry")
        => AddTrade(from, to, amount, classification, note, manual: true);

    public void MarkTradeWindowActive(string? playerName = null)
    {
        var now = DateTime.UtcNow;
        tradeWindowLikelyOpenUntilUtc = now.AddSeconds(30);
        trustedTradeSequenceActive = true;
        tradeAddonSeenDuringSequence |= IsTradeAddonOpen();
        if (!string.IsNullOrWhiteSpace(playerName))
            log.Add(LogCategory.Trades, $"Trade window activity detected for {playerName}.");
    }

    public void MarkTradeWindowClosed(bool cancelled = false)
    {
        if (cancelled) balanceEvidence?.Cancel();
        tradeWindowLikelyOpenUntilUtc = DateTime.MinValue;
        trustedTradeSequenceActive = false;
        tradeAddonSeenDuringSequence = false;
        observedTradeGilAmounts.Clear();
        lastTradeAmountObservedAtUtc = DateTime.MinValue;
        lastObservedTradeAmountSignature = string.Empty;
    }

    public void Tick()
    {
        if (!AutomaticDetectionEnabled)
        {
            MarkTradeWindowClosed(cancelled: true);
            balanceWindowVisible = false;
            balanceEvidence = null;
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextTradeWindowPollAtUtc)
            return;

        nextTradeWindowPollAtUtc = now.AddMilliseconds(100);
        ObserveNativeTradeBalance();

        if (!TryReadTradeWindowAmounts(out var amounts, out var diagnosticText))
        {
            if (trustedTradeSequenceActive)
            {
                var closedAfterOpening = tradeAddonSeenDuringSequence
                    && now - lastTradeWindowSeenAtUtc > TimeSpan.FromSeconds(2);
                if (closedAfterOpening)
                {
                    log.Add(LogCategory.Trades, "Trade window closed without a completion message; queued commands may resume.");
                    MarkTradeWindowClosed();
                    return;
                }
            }

            if (now - lastTradeWindowSeenAtUtc > TimeSpan.FromSeconds(2))
                observedTradeGilAmounts.Clear();
            return;
        }

        tradeAddonSeenDuringSequence = true;
        lastTradeWindowSeenAtUtc = now;
        tradeWindowLikelyOpenUntilUtc = now.AddSeconds(3);

        if (amounts.Count == 0)
            return;

        observedTradeGilAmounts.Clear();
        foreach (var amount in amounts)
            observedTradeGilAmounts.Add(amount);

        lastTradeAmountObservedAtUtc = now;
        var signature = string.Join("|", observedTradeGilAmounts.OrderBy(x => x));
        if (!string.Equals(signature, lastObservedTradeAmountSignature, StringComparison.Ordinal))
        {
            lastObservedTradeAmountSignature = signature;
            log.Add(LogCategory.Trades, $"Trade window gil observed: {string.Join(", ", observedTradeGilAmounts.OrderBy(x => x).Select(x => x.ToString("N0", CultureInfo.InvariantCulture)))}.");
        }
    }

    public bool TryAddDetectedIncomingTrade(string playerName, long amount, string note = "Detected confirmed incoming gil trade")
    {
        if (!AutomaticDetectionEnabled)
            return false;

        if (!TryResolveCurrentPartyMember(playerName, out var partyMember))
        {
            log.Add(LogCategory.Warnings, $"Ignored incoming gil trade from {playerName}: player is not currently in the party.");
            return false;
        }

        if (amount <= 0)
        {
            log.Add(LogCategory.Warnings, $"Ignored incoming gil line for {partyMember.Display}: amount was not positive.");
            return false;
        }

        if (BalanceAlreadyRecorded(partyMember, amount, incoming: true)) return true;
        var before = Trades.Count;
        AddDetectedIncomingTrade(partyMember.Display, amount, note);
        if (Trades.Count > before) RecordChatEvidence(partyMember, amount, incoming: true);
        return true;
    }

    public bool TryAddDetectedOutgoingTrade(string playerName, long amount, string note = "Detected confirmed outgoing gil trade")
    {
        if (!AutomaticDetectionEnabled)
            return false;

        if (!TryResolveCurrentPartyMember(playerName, out var partyMember))
        {
            log.Add(LogCategory.Warnings, $"Ignored outgoing gil trade to {playerName}: player is not currently in the party.");
            return false;
        }

        if (BalanceAlreadyRecorded(partyMember, amount, incoming: false)) return true;
        if (!IsGilAmountConfirmedByTradeWindow(amount, out var reason))
        {
            // The amount check polls native state and can finish the balance
            // path, which intentionally clears the old visible amount cache.
            if (BalanceAlreadyRecorded(partyMember, amount, incoming: false)) return true;
            log.Add(LogCategory.Warnings, $"Ignored outgoing gil line for {partyMember.Display}: {amount:N0} gil was not confirmed by the Trade window. {reason}");
            return false;
        }

        if (BalanceAlreadyRecorded(partyMember, amount, incoming: false)) return true;
        var before = Trades.Count;
        AddDetectedOutgoingTrade(partyMember.Display, amount, note);
        if (Trades.Count > before) RecordChatEvidence(partyMember, amount, incoming: false);
        return true;
    }

    public bool TryAddSystemConfirmedOutgoingTrade(string playerName, long amount, string note = "Detected confirmed outgoing gil trade from system log")
    {
        if (!AutomaticDetectionEnabled)
            return false;

        if (!TryResolveCurrentPartyMember(playerName, out var partyMember))
        {
            log.Add(LogCategory.Warnings, $"Ignored outgoing gil trade to {playerName}: player is not currently in the party.");
            return false;
        }

        if (amount <= 0)
        {
            log.Add(LogCategory.Warnings, $"Ignored outgoing gil line for {partyMember.Display}: amount was not positive.");
            return false;
        }

        if (BalanceAlreadyRecorded(partyMember, amount, incoming: false)) return true;
        var before = Trades.Count;
        AddDetectedOutgoingTrade(partyMember.Display, amount, note);
        if (Trades.Count > before) RecordChatEvidence(partyMember, amount, incoming: false);
        return true;
    }

    private bool BalanceAlreadyRecorded(PlayerIdentity partner, long amount, bool incoming)
    {
        ObserveNativeTradeBalance(); // Give the native balance path first chance, even between regular polls.
        return balanceEvidence is not null && SameIdentity(partner, balancePartner)
            && balanceEvidence.Matches(amount, incoming)
            && (incoming ? balanceEvidence.IncomingRecorded : balanceEvidence.OutgoingRecorded);
    }

    private void RecordChatEvidence(PlayerIdentity partner, long amount, bool incoming)
    {
        if (balanceEvidence is null || !SameIdentity(partner, balancePartner)) return;
        if (balanceEvidence.Matches(amount, incoming)) balanceEvidence.Record(incoming);
        else balanceEvidence.Cancel(); // Conflicting chat evidence must not add a second, different payment.
    }

    public string? CurrentNativeTradePartner => balanceEvidence is not null && DateTime.UtcNow - balancePartnerSeenAt < TimeSpan.FromSeconds(12)
        && IsExactCurrentPartyMember(balancePartner)
        ? balancePartner.Display : null;

    private void ObserveNativeTradeBalance()
    {
        if (!AutomaticDetectionEnabled) return;
        try
        {
            if (!DalamudServices.ClientState.IsLoggedIn || !DalamudServices.PlayerState.IsLoaded)
            {
                balanceEvidence?.Cancel();
                balanceWindowVisible = false;
                return;
            }
            var inventory = InventoryManager.Instance();
            if (inventory is null) return;
            var ptr = DalamudServices.GameGui.GetAddonByName("Trade");
            var addon = (AtkUnitBase*)ptr.Address;
            var visible = addon is not null && addon->IsVisible;
            if (visible && !addon->IsReady) return;
            var currency = inventory->GetInventoryContainer(InventoryType.Currency);
            long? ReadGil() => currency is not null && currency->IsLoaded ? inventory->GetGil() : null;
            if (visible && !balanceWindowVisible) balanceEvidence = null;
            // Addon creation can precede the native partner/currency data by a
            // frame. Retry only while offers are still being prepared, never
            // capture a new baseline after the final confirmation/payment.
            if (visible && balanceEvidence is null && inventory->TradeLocalState is TradeState.SelectingTradeGoods or TradeState.LockedIn)
            {
                // Native trade data supplies the same partner shown in Trade,
                // not the current target or an inferred chat name.
                var entityId = inventory->TradePartnerEntityId;
                var name = inventory->TradePartnerNameString.Trim();
                var candidates = DalamudServices.ObjectTable.PlayerObjects.OfType<IPlayerCharacter>()
                    .Where(player => player.EntityId == entityId && player.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 1 && ReadGil() is { } gil)
                {
                    var player = candidates[0];
                    var identity = new PlayerIdentity(name, player.HomeWorld.Value.Name.ExtractText());
                    if (IsExactCurrentPartyMember(identity))
                    {
                        balancePartner = identity;
                        balancePartnerEntityId = entityId;
                        balanceOperatorId = DalamudServices.PlayerState.ContentId;
                        balanceTerritoryId = DalamudServices.ClientState.TerritoryType;
                        balanceEvidence = new TradeBalanceEvidence(gil);
                        log.Add(LogCategory.Trades, $"Native trade opened with {identity.Display}; operator gil baseline={gil:N0}.");
                    }
                }
            }
            balanceWindowVisible = visible;
            if (balanceEvidence is null) return;
            if (DalamudServices.PlayerState.ContentId != balanceOperatorId || DalamudServices.ClientState.TerritoryType != balanceTerritoryId
                || !IsExactCurrentPartyMember(balancePartner))
            {
                balanceEvidence.Cancel();
                return;
            }
            if (visible && !balanceEvidence.Completed && inventory->TradeLocalState != TradeState.NotTrading)
            {
                balancePartnerSeenAt = DateTime.UtcNow;
                if (inventory->TradePartnerEntityId != balancePartnerEntityId
                    || !inventory->TradePartnerNameString.Trim().Equals(balancePartner.Name, StringComparison.OrdinalIgnoreCase))
                { balanceEvidence.Cancel(); return; }
                // FFXIVClientStructs documents slot 6 as gil on both sides.
                if (!inventory->TradeIsSyncPending && inventory->TradeLocalState is TradeState.SelectingTradeGoods or TradeState.LockedIn or TradeState.WaitingForConfirmation or TradeState.Confirmed)
                    balanceEvidence.ObserveOffer(inventory->TradeItemsRemote[5].Quantity, inventory->TradeItemsLocal[5].Quantity,
                        inventory->TradeLocalState is TradeState.WaitingForConfirmation or TradeState.Confirmed);
            }
            var promptPtr = DalamudServices.GameGui.GetAddonByName("SelectYesno");
            var prompt = (AddonSelectYesno*)promptPtr.Address;
            bool? promptVisible = prompt is null || !prompt->AtkUnitBase.IsVisible ? false
                : !prompt->AtkUnitBase.IsReady ? null
                : prompt->PromptText is not null && prompt->PromptText->NodeText.ToString().Contains("Complete trade", StringComparison.OrdinalIgnoreCase);
            if (balanceEvidence.Observe(balanceEvidence.NeedsBalanceRead ? ReadGil() : null, promptVisible, visible, DateTime.UtcNow,
                    inventory->TradeLocalState == TradeState.Confirmed))
                ApplyNativeBalanceTrade();
        }
        catch (Exception ex)
        {
            balanceEvidence?.Cancel();
            if (DateTime.UtcNow >= nextBalanceWarningAt)
            {
                nextBalanceWarningAt = DateTime.UtcNow.AddSeconds(5);
                log.Add(LogCategory.Warnings, $"Native trade balance verification unavailable; system-message detection remains active. {ex.Message}");
            }
        }
    }

    private void ApplyNativeBalanceTrade()
    {
        var evidence = balanceEvidence!;
        var dealer = session.SessionPlayers.FirstOrDefault(player => player.Status == PlayerStatus.Dealer
            && player.Identity.Name.Equals(DalamudServices.PlayerState.CharacterName, StringComparison.OrdinalIgnoreCase));
        var partner = session.SessionPlayers.FirstOrDefault(player => SameIdentity(player.Identity, balancePartner));
        if (dealer is null || !IsExactCurrentPartyMember(balancePartner)) return;
        if (partner is null && evidence.Incoming > 0)
        {
            partner = new PlayerSessionState { Identity = balancePartner, Status = PlayerStatus.Playing,
                PartySlot = Math.Max(2, session.SessionPlayers.Select(player => player.PartySlot).DefaultIfEmpty(1).Max() + 1) };
            session.SessionPlayers.Add(partner);
        }
        if (partner is null || partner.Status == PlayerStatus.Dealer) return;
        const string note = "Verified exact native trade offers and operator gil delta within 3 seconds of final confirmation; Trade window closed";
        if (evidence.Incoming > 0 && !evidence.IncomingRecorded)
        {
            evidence.Record(true);
            AddTrade(partner.Identity, dealer.Identity, evidence.Incoming, TradeClassification.BuyInBankDeposit, note, manual: false, verifiedNativeTrade: true);
        }
        if (evidence.Outgoing > 0 && !evidence.OutgoingRecorded)
        {
            evidence.Record(false);
            AddTrade(dealer.Identity, partner.Identity, evidence.Outgoing, TradeClassification.CashOut, note, manual: false, verifiedNativeTrade: true);
        }
        MarkTradeWindowClosed(); // Confirmed payment needs no additional chat timeout.
    }

    private static bool SameIdentity(PlayerIdentity left, PlayerIdentity right)
        => string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(left.World, right.World, StringComparison.OrdinalIgnoreCase);

    private static bool IsExactCurrentPartyMember(PlayerIdentity identity)
        => DalamudServices.PartyList.Count(member => SameIdentity(identity,
            new PlayerIdentity(member.Name.TextValue, member.World.ValueNullable?.Name.ExtractText() ?? string.Empty))) == 1;

    private TradeEntry AddDetectedIncomingTrade(string playerName, long amount, string note = "Detected from chat/log text")
    {
        var player = FindPlayerByName(playerName) ?? RehydrateTradePlayer(playerName);
        var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
        if (player is null || dealer is null)
        {
            var unknown = CreateIdentityFromDisplay(playerName);
            log.Add(LogCategory.Warnings, $"Detected possible incoming trade from {playerName} for {amount:N0} gil, but could not match them to a tracked table player.");
            return AddTrade(unknown, PlayerIdentity.UnknownDealer(), amount, TradeClassification.NeedsReview, note, manual: false);
        }

        return AddTrade(player.Identity, dealer.Identity, amount, TradeClassification.BuyInBankDeposit, note, manual: false);
    }


    private TradeEntry AddDetectedOutgoingTrade(string playerName, long amount, string note = "Detected outgoing cash-out from chat/log text")
    {
        var player = FindPlayerByName(playerName);
        var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
        if (player is null || dealer is null)
        {
            var unknown = new PlayerIdentity(playerName.Trim(), string.Empty);
            log.Add(LogCategory.Warnings, $"Detected possible outgoing trade to {playerName} for {amount:N0} gil, but could not match them to a tracked party player.");
            return AddTrade(PlayerIdentity.UnknownDealer(), unknown, amount, TradeClassification.NeedsReview, note, manual: false);
        }

        // 0.1.81: outgoing gil is a cash-out whenever it is detected. This lets
        // players cash out mid-round or between hands without phase-specific
        // bookkeeping blocking the bank update.
        return AddTrade(dealer.Identity, player.Identity, amount, TradeClassification.CashOut, note, manual: false);
    }

    public static bool TryResolveCurrentPartyMember(string playerName, out PlayerIdentity identity)
    {
        identity = default;
        var normalizedName = NormalizeName(playerName);
        if (string.IsNullOrWhiteSpace(normalizedName) || !DalamudServices.PlayerState.IsLoaded)
            return false;

        try
        {
            foreach (var member in DalamudServices.PartyList)
            {
                var memberName = member.Name.TextValue.Trim();
                if (!string.Equals(NormalizeName(memberName), normalizedName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var world = member.World.ValueNullable?.Name.ExtractText() ?? string.Empty;
                identity = new PlayerIdentity(memberName, world);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static bool TryResolveCurrentPartyMemberFromTradeText(string tradeText, out PlayerIdentity identity)
    {
        identity = default;
        var normalizedText = NormalizeName(tradeText);
        if (string.IsNullOrWhiteSpace(normalizedText) || !DalamudServices.PlayerState.IsLoaded)
            return false;

        try
        {
            // Prefer the longest matching party name so similarly prefixed
            // names cannot steal attribution from another current member.
            var matches = new List<(PlayerIdentity Identity, int NameLength)>();
            foreach (var member in DalamudServices.PartyList)
            {
                var memberName = member.Name.TextValue.Trim();
                var normalizedMemberName = NormalizeName(memberName);
                if (string.IsNullOrWhiteSpace(normalizedMemberName)
                    || !normalizedText.Contains(normalizedMemberName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var world = member.World.ValueNullable?.Name.ExtractText() ?? string.Empty;
                matches.Add((new PlayerIdentity(memberName, world), normalizedMemberName.Length));
            }

            if (matches.Count == 0)
                return false;

            identity = matches.OrderByDescending(match => match.NameLength).First().Identity;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsGilAmountConfirmedByTradeWindow(long amount, out string reason)
    {
        reason = string.Empty;
        if (amount <= 0)
        {
            reason = "Amount was not positive.";
            return false;
        }

        Tick();
        var now = DateTime.UtcNow;
        if (now - lastTradeAmountObservedAtUtc > TimeSpan.FromSeconds(12))
        {
            reason = "No recent Trade window gil amount was observed.";
            return false;
        }

        if (observedTradeGilAmounts.Contains(amount))
            return true;

        reason = observedTradeGilAmounts.Count == 0
            ? "The Trade window was observed, but no gil amount was visible."
            : $"Recent Trade window amounts were: {string.Join(", ", observedTradeGilAmounts.OrderBy(x => x).Select(x => x.ToString("N0", CultureInfo.InvariantCulture)))}.";
        return false;
    }

    private static bool TryReadTradeWindowAmounts(out HashSet<long> amounts, out string diagnosticText)
    {
        amounts = [];
        diagnosticText = string.Empty;

        if (!TryGetTradeAddon(out var tradeAddon))
            return false;

        var nodeList = tradeAddon->UldManager.NodeList;
        var nodeCount = Math.Min((int)tradeAddon->UldManager.NodeListCount, 512);
        if (nodeList is null || nodeCount <= 0)
            return true;

        var textParts = new List<string>();
        for (var i = 0; i < nodeCount; i++)
        {
            var node = nodeList[i];
            if (node is null || (uint)node->Type != 3)
                continue;

            var textNode = node->GetAsAtkTextNode();
            if (textNode is null)
                continue;

            var text = CleanTradeWindowText(textNode->NodeText.ToString());
            if (string.IsNullOrWhiteSpace(text))
                continue;

            textParts.Add(text);
            foreach (var amount in ReadPositiveGilLikeNumbers(text))
                amounts.Add(amount);
        }

        diagnosticText = string.Join(" | ", textParts.Take(8));
        return true;
    }

    private static bool TryGetTradeAddon(out AtkUnitBase* addon)
    {
        addon = null;
        try
        {
            var addonPtr = DalamudServices.GameGui.GetAddonByName("Trade");
            if (addonPtr.Address == nint.Zero)
                return false;

            addon = (AtkUnitBase*)addonPtr.Address;
            return addon is not null && addon->IsVisible && addon->IsReady;
        }
        catch
        {
            addon = null;
            return false;
        }
    }

    private static string CleanTradeWindowText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = new string(text
            .Replace(' ', ' ')
            .Where(c => !char.IsControl(c))
            .ToArray());

        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
    }

    private static IEnumerable<long> ReadPositiveGilLikeNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(text, @"(?<![A-Za-z0-9])(?:\d{1,3}(?:,\d{3})+|\d+)(?![A-Za-z0-9])").Cast<System.Text.RegularExpressions.Match>())
        {
            var raw = match.Value.Replace(",", string.Empty);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
                yield return value;
        }
    }

    public void Reclassify(TradeEntry entry, TradeClassification classification, string note)
    {
        // Reclassification currently logs the audit change. Bank-affecting corrections should be entered
        // as a manual correction/deposit so the delta is explicit and undoable.
        entry.Classification = classification;
        entry.Note = note;
        log.Add(LogCategory.Trades, $"Trade {entry.Id} reclassified as {classification}: {note}");
    }

    private TradeEntry AddTrade(PlayerIdentity from, PlayerIdentity to, long amount, TradeClassification classification, string note, bool manual, bool verifiedNativeTrade = false)
    {
        amount = Math.Max(0, amount);
        if (!manual && !verifiedNativeTrade && IsDuplicateAutomaticTrade(from, to, amount, classification, out var duplicate))
        {
            log.Add(LogCategory.Warnings, $"Ignored duplicate automatic trade: {from.Display} → {to.Display} {amount:N0} gil as {classification}.");
            return duplicate;
        }

        var entry = new TradeEntry
        {
            From = from,
            To = to,
            Amount = amount,
            Classification = classification,
            Phase = session.Round.Phase.ToString(),
            Note = note,
            Manual = manual
        };

        Trades.Add(entry);
        if (!manual)
            RememberAutomaticTrade(entry);
        ApplyTradeEffects(entry);
        ledger.RecordTrade(entry);
        log.Add(LogCategory.Trades, $"{(manual ? "Manual" : "Detected")} trade entered: {from.Display} → {to.Display} {amount:N0} gil as {classification}.");
        return entry;
    }

    private static bool IsTradeAddonOpen()
        => TryGetTradeAddon(out _);

    private bool IsDuplicateAutomaticTrade(
        PlayerIdentity from,
        PlayerIdentity to,
        long amount,
        TradeClassification classification,
        out TradeEntry duplicate)
    {
        var now = DateTime.UtcNow;
        foreach (var staleKey in recentAutomaticTrades
                     .Where(pair => now - pair.Value > DuplicateTradeWindow)
                     .Select(pair => pair.Key)
                     .ToList())
            recentAutomaticTrades.Remove(staleKey);

        var key = BuildAutomaticTradeKey(from, to, amount, classification);
        if (!recentAutomaticTrades.TryGetValue(key, out var recordedAt)
            || now - recordedAt > DuplicateTradeWindow)
        {
            duplicate = null!;
            return false;
        }

        duplicate = Trades.LastOrDefault(entry =>
            !entry.Manual
            && entry.Amount == amount
            && entry.Classification == classification
            && string.Equals(NormalizeName(entry.From.Display), NormalizeName(from.Display), StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeName(entry.To.Display), NormalizeName(to.Display), StringComparison.OrdinalIgnoreCase))
            ?? new TradeEntry
            {
                From = from,
                To = to,
                Amount = amount,
                Classification = TradeClassification.IgnoreDuplicateInvalid,
                Phase = session.Round.Phase.ToString(),
                Note = "Duplicate automatic trade ignored"
            };
        return true;
    }

    private void RememberAutomaticTrade(TradeEntry entry)
        => recentAutomaticTrades[BuildAutomaticTradeKey(entry.From, entry.To, entry.Amount, entry.Classification)] = DateTime.UtcNow;

    private static string BuildAutomaticTradeKey(
        PlayerIdentity from,
        PlayerIdentity to,
        long amount,
        TradeClassification classification)
        => $"{classification}|{NormalizeName(from.Display)}|{NormalizeName(to.Display)}|{amount}";

    private void ApplyTradeEffects(TradeEntry entry)
    {
        switch (entry.Classification)
        {
            case TradeClassification.BuyInBankDeposit:
            case TradeClassification.Bet:
                if (TryGetPlayer(entry.From, out var depositor))
                {
                    depositor.Bank.Available += entry.Amount;
                    depositor.Bank.LastTradeAmount = entry.Amount;
                    if (depositor.Status is PlayerStatus.SittingOut or PlayerStatus.CashedOut or PlayerStatus.LeftDisconnected)
                        depositor.Status = PlayerStatus.Playing;
                    log.Add(LogCategory.Trades, $"Bank deposit applied: {depositor.DisplayName} +{entry.Amount:N0} gil.");
                }
                break;
            case TradeClassification.CashOut:
                if (TryGetPlayer(entry.To, out var cashedOutPlayer))
                {
                    // A wager reserved during BettingOpen has not entered a live hand
                    // yet. Cancel it before applying an outgoing cash-out so the trade
                    // reduces the player's complete tracked bank instead of leaving the
                    // reserved portion stranded as an active bet.
                    if (session.Round.Phase == BlackjackPhase.BettingOpen && cashedOutPlayer.Bank.ActiveBet > 0)
                    {
                        var canceledBet = cashedOutPlayer.Bank.ActiveBet;
                        cashedOutPlayer.Bank.Available += canceledBet;
                        cashedOutPlayer.Bank.ActiveBet = 0;
                        cashedOutPlayer.BetConfirmed = false;
                        cashedOutPlayer.Hands.Clear();
                        log.Add(LogCategory.RoundFlow, $"Canceled {canceledBet:N0} gil pending bet for {cashedOutPlayer.DisplayName} before cash-out.");
                    }

                    var trackedBefore = cashedOutPlayer.Bank.TotalTracked;
                    cashedOutPlayer.Bank.Available = Math.Max(0, cashedOutPlayer.Bank.Available - entry.Amount);
                    cashedOutPlayer.Bank.LastTradeAmount = entry.Amount;
                    if (cashedOutPlayer.Bank.TotalTracked == 0)
                        cashedOutPlayer.Status = PlayerStatus.CashedOut;
                    log.Add(LogCategory.Trades, $"Cash-out applied: {cashedOutPlayer.DisplayName} -{Math.Min(entry.Amount, trackedBefore):N0} tracked gil; bank is now {cashedOutPlayer.Bank.TotalTracked:N0} gil.");
                    if (entry.Amount > trackedBefore)
                        log.Add(LogCategory.Warnings, $"Cash-out for {cashedOutPlayer.DisplayName} exceeded the tracked bank by {entry.Amount - trackedBefore:N0} gil; the player bank was clamped to 0.");
                }
                break;
        }
    }

    private PlayerSessionState? FindPlayerByName(string name)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var exact = session.SessionPlayers.FirstOrDefault(p =>
            NormalizeName(p.Identity.Name) == normalized
            || NormalizeName(p.DisplayName) == normalized);
        if (exact is not null)
            return exact;

        var compact = normalized.Replace(" ", string.Empty);
        return session.SessionPlayers
            .Where(p => p.Status != PlayerStatus.Dealer)
            .FirstOrDefault(p =>
            {
                var identityName = NormalizeName(p.Identity.Name);
                var displayName = NormalizeName(p.DisplayName);
                var identityCompact = identityName.Replace(" ", string.Empty);
                var displayCompact = displayName.Replace(" ", string.Empty);

                return displayName.StartsWith(normalized + "@", StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(identityName + " ", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(displayName, StringComparison.OrdinalIgnoreCase)
                    || displayName.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || compact == identityCompact
                    || compact == displayCompact;
            });
    }

    public PlayerSessionState? ResolveTrackedTradeCandidateByName(string name) => FindPlayerByName(name);

    public PlayerSessionState? ResolveCurrentTargetTradeCandidate()
    {
        try
        {
            var targetName = DalamudServices.TargetManager.Target?.Name.TextValue.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetName))
                return null;

            return FindPlayerByName(targetName);
        }
        catch
        {
            return null;
        }
    }

    public PlayerSessionState? ResolveSingleActivePartyPlayer() => ResolveSingleTrackedTradeCandidate();

    public PlayerSessionState? ResolveSingleTrackedTradeCandidate()
    {
        // Trade log lines often lose the player name and may still arrive after the
        // player has left the party. Keep disconnected tracked players eligible for
        // amount-only attribution, but only when there is exactly one safe candidate.
        var candidates = session.SessionPlayers
            .Where(p => p.Status != PlayerStatus.Dealer && p.Status != PlayerStatus.SpectatorStaff)
            .ToList();

        var activeOrFunded = candidates
            .Where(p => p.Status != PlayerStatus.CashedOut || p.Bank.TotalTracked > 0)
            .ToList();

        if (activeOrFunded.Count == 1)
            return activeOrFunded[0];

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool TryGetPlayer(PlayerIdentity identity, out PlayerSessionState player)
    {
        var match = session.SessionPlayers.FirstOrDefault(p => p.Identity.Equals(identity))
            ?? session.SessionPlayers.FirstOrDefault(p => NormalizeName(p.Identity.Name) == NormalizeName(identity.Name))
            ?? session.SessionPlayers.FirstOrDefault(p => NamesLooselyMatch(p.DisplayName, identity.Display));

        if (match is null)
        {
            player = null!;
            return false;
        }

        player = match;
        return true;
    }


    private PlayerSessionState? RehydrateTradePlayer(string playerName)
    {
        var identity = CreateIdentityFromDisplay(playerName);
        if (string.IsNullOrWhiteSpace(identity.Name))
            return null;

        if (session.SessionPlayers.Any(p => p.Status == PlayerStatus.Dealer && NamesLooselyMatch(p.DisplayName, identity.Display)))
            return null;

        var existing = FindPlayerByName(identity.Display) ?? FindPlayerByName(identity.Name);
        if (existing is not null)
            return existing;

        var nextSlot = Math.Max(2, session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer).Select(p => p.PartySlot).DefaultIfEmpty(1).Max() + 1);
        var player = new PlayerSessionState
        {
            Identity = identity,
            PartySlot = nextSlot,
            Status = PlayerStatus.Playing
        };

        session.SessionPlayers.Add(player);
        log.Add(LogCategory.Trades, $"Re-added trade player {player.DisplayName} from detected trade text.");
        return player;
    }

    private static PlayerIdentity CreateIdentityFromDisplay(string display)
    {
        var cleaned = display.Trim();
        var atIndex = cleaned.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0)
        {
            var name = cleaned[..atIndex].Trim();
            var world = cleaned[(atIndex + 1)..].Trim();
            return new PlayerIdentity(name, world);
        }

        var worldName = GetLocalWorldName();
        return new PlayerIdentity(cleaned, worldName);
    }

    private static string GetLocalWorldName()
    {
        try
        {
            return DalamudServices.PlayerState.IsLoaded
                ? DalamudServices.PlayerState.HomeWorld.Value.Name.ExtractText()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool NamesLooselyMatch(string left, string right)
    {
        var a = NormalizeName(left);
        var b = NormalizeName(right);
        return !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b)
            && (a == b || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var characterName = value.Trim();
        var atIndex = characterName.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
            characterName = characterName[..atIndex];

        Span<char> buffer = stackalloc char[Math.Min(characterName.Length, 64)];
        var count = 0;
        foreach (var ch in characterName)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;
            if (count >= buffer.Length)
                break;
            buffer[count++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..count]);
    }
}
